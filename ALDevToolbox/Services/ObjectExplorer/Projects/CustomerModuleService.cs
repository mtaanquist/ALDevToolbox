using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Which third-party modules a customer has. The organisation keeps a short catalogue;
/// an online solution is matched against what Business Central last reported as
/// installed, an on-premises one has its modules typed in. See
/// <c>.design/solution-customer-info.md</c>, "Modules".
/// </summary>
public sealed class CustomerModuleService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<CustomerModuleService> _logger;

    public CustomerModuleService(
        AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<CustomerModuleService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    // ── The catalogue ───────────────────────────────────────────────────

    /// <summary>The catalogue by name, with how many solutions have each module either way.</summary>
    public async Task<List<CatalogModule>> ListCatalogAsync(CancellationToken ct = default)
    {
        var modules = await _db.CustomerModules.AsNoTracking().ToListAsync(ct);
        var typed = await _db.OeProjectModules.AsNoTracking()
            .Where(m => m.Project!.DeletedAt == null)
            .GroupBy(m => m.ModuleId)
            .Select(g => new { ModuleId = g.Key, Count = g.Select(m => m.ProjectId).Distinct().Count() })
            .ToDictionaryAsync(g => g.ModuleId, g => g.Count, ct);
        var appIds = modules.Where(m => m.AppId is not null).Select(m => m.AppId!.Value).ToList();
        var installed = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => appIds.Contains(a.AppId) && a.Environment!.Project!.DeletedAt == null)
            .GroupBy(a => a.AppId)
            .Select(g => new { AppId = g.Key, Count = g.Select(a => a.Environment!.ProjectId).Distinct().Count() })
            .ToDictionaryAsync(g => g.AppId, g => g.Count, ct);

        return modules
            .Select(m => new CatalogModule(m.Id, m.Name, m.Publisher, m.AppId,
                typed.GetValueOrDefault(m.Id) + (m.AppId is { } id ? installed.GetValueOrDefault(id) : 0)))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Apps Business Central has reported somewhere in the organisation that are not in
    /// the catalogue yet, most widespread first - so adding a module is a pick, not a
    /// GUID typed from memory. Microsoft's own apps are left out: every customer has them.
    /// </summary>
    public async Task<List<SeenApp>> ListSeenAppsAsync(CancellationToken ct = default)
    {
        var known = await _db.CustomerModules.AsNoTracking()
            .Where(m => m.AppId != null).Select(m => m.AppId!.Value).ToListAsync(ct);
        var rows = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => !known.Contains(a.AppId) && a.Publisher != "Microsoft")
            .GroupBy(a => new { a.AppId, a.Name, a.Publisher })
            .Select(g => new { g.Key.AppId, g.Key.Name, g.Key.Publisher, Solutions = g.Select(a => a.Environment!.ProjectId).Distinct().Count() })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.AppId)
            .Select(g => g.OrderByDescending(r => r.Solutions).First())
            .OrderByDescending(r => r.Solutions).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SeenApp(r.AppId, r.Name, r.Publisher, r.Solutions))
            .ToList();
    }

    public async Task SaveCatalogModuleAsync(int? moduleId, CatalogModuleInput input, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing the module catalogue needs an authenticated request.");

        var errors = new Dictionary<string, string>();
        var name = Clean(input.Name);
        var publisher = Clean(input.Publisher);
        Guid? appId = null;

        if (name is null) errors["Name"] = "Enter the module's name.";
        else if (name.Length > 250) errors["Name"] = "Keep the name under 250 characters.";
        if (publisher?.Length > 250) errors["Publisher"] = "Keep the publisher under 250 characters.";
        if (Clean(input.AppId) is { } typed)
        {
            if (Guid.TryParse(typed, out var parsed)) appId = parsed;
            else errors["AppId"] = "An app ID looks like 11111111-2222-3333-4444-555555555555. Leave it empty for a module that isn't a Business Central app.";
        }

        if (name is not null && await _db.CustomerModules.AnyAsync(m => m.Name == name && m.Id != moduleId, ct))
            errors["Name"] = "There is already a module with that name.";
        if (appId is { } id && await _db.CustomerModules.AnyAsync(m => m.AppId == id && m.Id != moduleId, ct))
            errors["AppId"] = "Another module in the catalogue already has that app ID.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        CustomerModule row;
        if (moduleId is { } existing)
        {
            row = await _db.CustomerModules.FirstOrDefaultAsync(m => m.Id == existing, ct)
                ?? throw Invalid("Name", "That module no longer exists.");
        }
        else
        {
            row = new CustomerModule { OrganizationId = orgId, CreatedAt = DateTime.UtcNow };
            _db.CustomerModules.Add(row);
        }

        row.Name = name!;
        row.Publisher = publisher;
        row.AppId = appId;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Saved customer module {ModuleName}.", name);
    }

    /// <summary>Removes a module from the catalogue, and with it every version typed against it.</summary>
    public async Task DeleteCatalogModuleAsync(int moduleId, CancellationToken ct = default)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing the module catalogue needs an authenticated request.");
        var row = await _db.CustomerModules.FirstOrDefaultAsync(m => m.Id == moduleId, ct);
        if (row is null) return;
        _db.CustomerModules.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── One solution's modules ──────────────────────────────────────────

    /// <summary>
    /// What a solution has. For an online solution that is the catalogue matched against
    /// what its production environments last reported - by app id, or by name and
    /// publisher for a catalogue entry without one. For an on-premises one it is what
    /// somebody typed.
    /// </summary>
    public async Task<SolutionModules> GetSolutionModulesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.HostingType })
            .FirstOrDefaultAsync(ct);
        if (project is null) return new SolutionModules(false, [], null, null, 0);

        var catalogCount = await _db.CustomerModules.CountAsync(ct);
        var onPremises = project.HostingType is not (null or ProjectHostingType.MicrosoftCloud);
        if (onPremises)
        {
            var typed = await _db.OeProjectModules.AsNoTracking()
                .Where(m => m.ProjectId == projectId)
                .Select(m => new SolutionModule(m.Id, m.ModuleId, m.Module!.Name, m.Module.Publisher, m.Version, m.Note, null))
                .ToListAsync(ct);
            return new SolutionModules(false, Sorted(typed), null, null, catalogCount);
        }

        // Production first; a solution with only sandboxes still has something to show.
        var environments = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.MissingSince == null)
            .Select(e => new { e.Id, e.Name, e.Type })
            .ToListAsync(ct);
        var chosen = environments
            .OrderByDescending(e => string.Equals(e.Type, "Production", StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (chosen is null) return new SolutionModules(true, [], null, null, catalogCount);

        var apps = await _db.OeEnvironmentApps.AsNoTracking().Where(a => a.EnvironmentId == chosen.Id).ToListAsync(ct);
        var catalog = await _db.CustomerModules.AsNoTracking().ToListAsync(ct);
        var matched = new List<SolutionModule>();
        foreach (var module in catalog)
        {
            var app = module.AppId is { } id
                ? apps.FirstOrDefault(a => a.AppId == id)
                : apps.FirstOrDefault(a =>
                    string.Equals(a.Name, module.Name, StringComparison.OrdinalIgnoreCase)
                    && (module.Publisher is null || string.Equals(a.Publisher, module.Publisher, StringComparison.OrdinalIgnoreCase)));
            if (app is not null)
                matched.Add(new SolutionModule(0, module.Id, module.Name, module.Publisher ?? app.Publisher, app.Version, null, chosen.Id));
        }

        return new SolutionModules(true, Sorted(matched), chosen.Name,
            apps.Count == 0 ? null : apps.Max(a => a.FetchedAt), catalogCount, chosen.Id);
    }

    /// <summary>Records a catalogue module on an on-premises solution, or changes its version and note.</summary>
    public async Task SaveSolutionModuleAsync(int projectId, int? rowId, SolutionModuleInput input, CancellationToken ct = default)
    {
        var project = await LoadManagedProjectAsync(projectId, ct);
        if (!project.IsOnPremises)
            throw Invalid("ModuleId", "This solution is on Business Central online, so its modules are read from the environment rather than typed in.");

        var errors = new Dictionary<string, string>();
        var version = Clean(input.Version);
        var note = Clean(input.Note);
        if (version?.Length > 50) errors["Version"] = "Keep the version under 50 characters.";
        if (note?.Length > 250) errors["Note"] = "Keep the note under 250 characters.";
        if (input.ModuleId is not { } moduleId || !await _db.CustomerModules.AnyAsync(m => m.Id == moduleId, ct))
            errors["ModuleId"] = "Choose a module from the catalogue.";
        else if (await _db.OeProjectModules.AnyAsync(m => m.ProjectId == projectId && m.ModuleId == moduleId && m.Id != rowId, ct))
            errors["ModuleId"] = "That module is already listed. Change its version there.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectModule row;
        if (rowId is { } id)
        {
            row = await _db.OeProjectModules.FirstOrDefaultAsync(m => m.Id == id && m.ProjectId == projectId, ct)
                ?? throw Invalid("ModuleId", "That entry no longer exists.");
        }
        else
        {
            row = new OeProjectModule { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectModules.Add(row);
        }

        row.ModuleId = input.ModuleId!.Value;
        row.Version = version;
        row.Note = note;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSolutionModuleAsync(int projectId, int rowId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectModules.FirstOrDefaultAsync(m => m.Id == rowId && m.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectModules.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The ids of the solutions that have a module, typed in or installed - the Solutions
    /// list's filter. Callers intersect it with what the viewer may see.
    /// </summary>
    public async Task<HashSet<int>> ListProjectIdsWithModuleAsync(int moduleId, CancellationToken ct = default)
    {
        var module = await _db.CustomerModules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moduleId, ct);
        if (module is null) return [];

        var ids = await _db.OeProjectModules.AsNoTracking()
            .Where(m => m.ModuleId == moduleId).Select(m => m.ProjectId).ToListAsync(ct);
        if (module.AppId is { } appId)
        {
            ids.AddRange(await _db.OeEnvironmentApps.AsNoTracking()
                .Where(a => a.AppId == appId && a.Environment!.MissingSince == null)
                .Select(a => a.Environment!.ProjectId).ToListAsync(ct));
        }
        return ids.ToHashSet();
    }

    private static List<SolutionModule> Sorted(IEnumerable<SolutionModule> modules) =>
        modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private async Task<OeProject> LoadManagedProjectAsync(int projectId, CancellationToken ct)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing a solution's modules needs an authenticated request.");
        var project = await _db.OeProjects.FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Invalid("ModuleId", "This solution no longer exists.");
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);
        return project;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PlanValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

public sealed record CatalogModule(int Id, string Name, string? Publisher, Guid? AppId, int Solutions);
public sealed record CatalogModuleInput(string? Name, string? Publisher, string? AppId);

/// <summary>An app some environment reported that the catalogue does not have yet.</summary>
public sealed record SeenApp(Guid AppId, string Name, string Publisher, int Solutions);

/// <param name="RowId">The typed row's id, or 0 for a module read from an environment.</param>
/// <param name="EnvironmentId">Where it was read from, for an online solution.</param>
public sealed record SolutionModule(int RowId, int ModuleId, string Name, string? Publisher, string? Version, string? Note, int? EnvironmentId);

public sealed record SolutionModuleInput(int? ModuleId, string? Version, string? Note);

/// <param name="ReadFromEnvironment">True for an online solution, whose list is read rather than typed.</param>
/// <param name="EnvironmentName">The environment the list came from, when there is one.</param>
/// <param name="FetchedAt">When Business Central last answered; null when it has not been read yet.</param>
/// <param name="CatalogCount">How many modules the catalogue has, so an empty list can say why it is empty.</param>
public sealed record SolutionModules(
    bool ReadFromEnvironment,
    List<SolutionModule> Modules,
    string? EnvironmentName,
    DateTime? FetchedAt,
    int CatalogCount,
    int? EnvironmentId = null);
