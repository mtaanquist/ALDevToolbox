using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// CRUD over <see cref="Project"/> and its <see cref="ProjectRepository"/>
/// children — the admin surface that defines what the project-build pipeline
/// clones and compiles. Org-scoped via the EF query filter; mutations run inside
/// an authenticated request (<see cref="RequireOrganizationId"/> throws
/// otherwise). Validation throws <see cref="PlanValidationException"/> with
/// field-keyed errors so the form renders them inline. See
/// <c>.design/object-explorer-project-builds.md</c>.
/// </summary>
public sealed class ProjectService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ProjectDiscoveryService _discovery;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ProjectDiscoveryService discovery,
        ILogger<ProjectService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _discovery = discovery;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; project mutation called outside an authenticated request.");

    /// <summary>
    /// True when the current user may manage <paramref name="projectId"/> (owner or
    /// org Admin / SiteAdmin) — for the UI to hide Build/Add/Delete affordances.
    /// Returns false when the project no longer exists.
    /// </summary>
    public async Task<bool> CanManageAsync(int projectId, CancellationToken ct = default)
    {
        var owner = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Select(c => new { c.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        return owner is not null && await _access.CanManageAsync(projectId, owner.CreatedByUserId, ct);
    }

    /// <summary>
    /// Active (non-deleted) projects the current user may see, repositories
    /// included, ordered by name. Private projects the caller has no grant on are
    /// left out entirely — this feeds project <em>pickers</em> (new pipeline, new
    /// release pipeline), where a name you can't act on is only in the way. The
    /// locked-name row lives in <see cref="ArtifactService.ListProjectsAsync"/>,
    /// which is what <c>/projects</c> renders.
    /// </summary>
    public async Task<List<Project>> ListProjectsAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .Include(c => c.Repositories)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// A single active project with its repositories, or null when not found in this
    /// org. Throws <see cref="ProjectAccessDeniedException"/> when the project is
    /// Private and the caller has no grant on it; the detail page renders that as
    /// its not-found state.
    /// </summary>
    public async Task<Project?> GetProjectAsync(int id, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(id, ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(c => c.Id == id && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The releases this project's builds produced, newest first — linked via the
    /// import job's <see cref="ImportJob.ProjectId"/> (a project Release carries
    /// no FK back to the project, only a name). Drives the project detail page's
    /// build history.
    /// </summary>
    public async Task<List<ProjectReleaseRow>> ListProjectReleasesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        var releaseIds = await _db.OeImportJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId)
            .Select(j => j.ReleaseId)
            .Distinct()
            .ToListAsync(ct);
        if (releaseIds.Count == 0) return new List<ProjectReleaseRow>();

        return await _db.OeReleases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .OrderByDescending(r => r.ImportedAt)
            .Select(r => new ProjectReleaseRow(r.Id, r.Label, r.Status, r.BcVersion, r.ImportedAt, r.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>Creates a project and its repositories. Returns the new id.</summary>
    public async Task<int> CreateProjectAsync(ProjectInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, country, repos) = await ValidateAsync(input, existingId: null, orgId, ct);

        var now = DateTime.UtcNow;
        var project = new Project
        {
            OrganizationId = orgId,
            Name = name,
            DefaultArtifactCountry = country,
            // The creator owns the project: they (or an org Admin) manage repos,
            // settings, builds, and deletion. See .design/artifacts.md.
            CreatedByUserId = _orgContext.CurrentUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories = repos.Select(r => new ProjectRepository
            {
                OrganizationId = orgId,
                Provider = r.Provider,
                Url = r.Url,
                DisplayName = r.DisplayName,
            }).ToList(),
        };
        _db.OeProjects.Add(project);
        await SaveTranslatingNameClashAsync(ct);

        _logger.LogInformation("Created project {ProjectId} ({Name}) with {RepoCount} repo(s) for org {OrgId}.",
            project.Id, name, project.Repositories.Count, orgId);

        // Warm the discovered-extensions cache in the background so the first
        // pipeline editor open is instant. Best-effort — a discovery enqueue
        // failure must never sink the create.
        if (project.Repositories.Count > 0) await WarmDiscoveryAsync(project.Id, ct);
        return project.Id;
    }

    /// <summary>
    /// Updates a project's name/country and replaces its repository set with the
    /// posted one (the form owns the whole list, so a save is a full replace).
    /// </summary>
    public async Task UpdateProjectAsync(int id, ProjectInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, country, repos) = await ValidateAsync(input, existingId: id, orgId, ct);

        var project = await _db.OeProjects
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This project no longer exists.");

        // Only the owner, an org Admin, or an assigned team may edit settings /
        // change the repo set.
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);

        project.Name = name;
        project.DefaultArtifactCountry = country;
        project.UpdatedAt = DateTime.UtcNow;

        // Full replace: drop the old rows, add the posted set. Repos are cheap and
        // identity-free from the form's perspective, so we don't diff in place.
        _db.OeProjectRepositories.RemoveRange(project.Repositories);
        project.Repositories = repos.Select(r => new ProjectRepository
        {
            OrganizationId = orgId,
            ProjectId = project.Id,
            Provider = r.Provider,
            Url = r.Url,
            DisplayName = r.DisplayName,
        }).ToList();

        await SaveTranslatingNameClashAsync(ct);
        _logger.LogInformation("Updated project {ProjectId} ({Name}); now {RepoCount} repo(s).",
            project.Id, name, project.Repositories.Count);

        // The repo set may have changed — re-warm the discovery cache in the
        // background so the pipeline editor reflects it. Best-effort.
        if (project.Repositories.Count > 0) await WarmDiscoveryAsync(project.Id, ct);
    }

    /// <summary>
    /// Best-effort enqueue of a background discovery refresh after a repo change.
    /// Swallows failures (the access check already passed for this caller) so a
    /// cache warm never fails the project save it follows.
    /// </summary>
    private async Task WarmDiscoveryAsync(int projectId, CancellationToken ct)
    {
        try
        {
            await _discovery.RequestDiscoveryAsync(projectId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue extension discovery for project {ProjectId} after a repo change.", projectId);
        }
    }

    // ── Access (visibility + assigned teams) ────────────────────────────

    /// <summary>
    /// The project's current access setting: its visibility level and the ids of the
    /// teams assigned to it. Readable by anyone who may see the project.
    /// </summary>
    public async Task<ProjectAccessSettings> GetAccessAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        var visibility = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => (ProjectVisibility?)p.Visibility)
            .FirstOrDefaultAsync(ct);

        var teamIds = await _db.OeProjectTeams.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => t.TeamId)
            .ToListAsync(ct);

        return new ProjectAccessSettings(visibility ?? ProjectVisibility.Public, teamIds);
    }

    /// <summary>
    /// Sets a project's visibility and the teams assigned to it, in one write.
    /// </summary>
    /// <remarks>
    /// <para>The two halves move together on purpose. The invariant is
    /// <c>Visibility != Public</c> ⇔ at least one team assigned, and two independent
    /// writes could interleave into a Private project with no team — a project
    /// nobody but an admin could reach. So a non-Public level with no teams is
    /// refused (field key <c>Teams</c>), and so is Public <em>with</em> teams: an
    /// assignment that grants nothing is a setting the user will misread later.</para>
    /// <para>A team id from another organisation is simply not found (the query
    /// filter), which is the right answer and the right error.</para>
    /// <para>Restricted to whoever may manage the project. See
    /// <c>.design/teams-and-visibility.md</c>.</para>
    /// </remarks>
    public async Task SetAccessAsync(
        int projectId, ProjectVisibility visibility, IReadOnlyList<int> teamIds, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        ArgumentNullException.ThrowIfNull(teamIds);

        var project = await _db.OeProjects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Validation("Visibility", "This project no longer exists.");

        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        var wanted = teamIds.Distinct().ToList();

        if (visibility == ProjectVisibility.Public && wanted.Count > 0)
        {
            throw Validation("Teams",
                "A public project is open to everyone, so it can't have teams. Remove the teams, or pick a different visibility.");
        }
        if (visibility != ProjectVisibility.Public && wanted.Count == 0)
        {
            throw Validation("Teams", "Pick at least one team that keeps access to this project.");
        }

        if (wanted.Count > 0)
        {
            var known = await _db.Teams.AsNoTracking()
                .Where(t => wanted.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);
            if (known.Count != wanted.Count)
            {
                throw Validation("Teams", "One of those teams no longer exists. Reload the page and try again.");
            }
        }

        var existing = await _db.OeProjectTeams
            .Where(t => t.ProjectId == projectId)
            .ToListAsync(ct);

        // Diff rather than replace wholesale: an unchanged assignment shouldn't
        // churn the audit log with a delete plus an identical insert.
        var removed = existing.Where(t => !wanted.Contains(t.TeamId)).ToList();
        if (removed.Count > 0) _db.OeProjectTeams.RemoveRange(removed);

        var now = DateTime.UtcNow;
        foreach (var teamId in wanted.Where(id => existing.All(t => t.TeamId != id)))
        {
            _db.OeProjectTeams.Add(new ProjectTeam
            {
                OrganizationId = orgId,
                ProjectId = projectId,
                TeamId = teamId,
                CreatedAt = now,
            });
        }

        project.Visibility = visibility;
        project.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Set project {ProjectId} visibility to {Visibility} with {TeamCount} team(s).",
            projectId, visibility, wanted.Count);
    }

    // ── Supplemental symbols (manual-symbols recovery) ──────────────────

    /// <summary>
    /// The operator-supplied dependency symbols stored for a project, newest
    /// first. Read-only projection (no blob) for the admin list. See
    /// <c>.design/object-explorer-project-builds.md</c> ("Manual-symbols recovery").
    /// </summary>
    public async Task<List<ProjectSymbolRow>> ListSupplementalSymbolsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        return await _db.OeProjectSymbols.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ProjectSymbolRow(s.Id, s.FileName, s.ContentLength, s.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stores one or more uploaded <c>.app</c> dependency symbols for a project,
    /// replacing any existing entry with the same file name (so re-uploading a
    /// corrected package overwrites rather than duplicates). Returns the number
    /// of packages saved. Validates that every upload is a non-empty <c>.app</c>;
    /// throws <see cref="PlanValidationException"/> (field key <c>Symbols</c>)
    /// otherwise so the manage page renders the error inline.
    /// </summary>
    public async Task<int> AddSupplementalSymbolsAsync(
        int projectId, IReadOnlyList<SupplementalSymbolUpload> uploads, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        ArgumentNullException.ThrowIfNull(uploads);

        var project = await _db.OeProjects.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == projectId && c.DeletedAt == null, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Symbols"] = "This project no longer exists." });

        await _access.EnsureCanManageAsync(projectId, project.CreatedByUserId, ct);

        if (uploads.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Symbols"] = "Choose at least one .app symbol package to upload.",
            });
        }

        // Normalise + validate. A duplicate name within one batch collapses to the
        // last upload, mirroring the per-project file-name uniqueness.
        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in uploads)
        {
            var name = (u.FileName ?? string.Empty).Trim();
            if (name.Length == 0 || !name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Symbols"] = $"\"{(name.Length == 0 ? "(unnamed)" : name)}\" isn't a .app file. Upload the dependency's compiled symbol package.",
                });
            }
            if (u.Content is not { Length: > 0 })
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Symbols"] = $"\"{name}\" is empty.",
                });
            }
            staged[name] = u.Content;
        }

        // Replace same-named rows so a re-upload overwrites in place.
        var names = staged.Keys.ToList();
        var existing = await _db.OeProjectSymbols
            .Where(s => s.ProjectId == projectId && names.Contains(s.FileName))
            .ToListAsync(ct);
        if (existing.Count > 0) _db.OeProjectSymbols.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var (name, content) in staged)
        {
            _db.OeProjectSymbols.Add(new ProjectSymbol
            {
                OrganizationId = orgId,
                ProjectId = projectId,
                FileName = name,
                Content = content,
                ContentLength = content.Length,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Stored {Count} supplemental symbol(s) for project {ProjectId} ({Name}).",
            staged.Count, projectId, project.Name);
        return staged.Count;
    }

    /// <summary>Removes one stored supplemental symbol from a project. No-op if it's already gone.</summary>
    public async Task DeleteSupplementalSymbolAsync(int projectId, int symbolId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId)
            .Select(c => c.CreatedByUserId)
            .FirstOrDefaultAsync(ct);
        await _access.EnsureCanManageAsync(projectId, ownerId, ct);

        var symbol = await _db.OeProjectSymbols
            .FirstOrDefaultAsync(s => s.Id == symbolId && s.ProjectId == projectId, ct);
        if (symbol is null) return;
        _db.OeProjectSymbols.Remove(symbol);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Removed supplemental symbol {SymbolId} ({File}) from project {ProjectId}.",
            symbolId, symbol.FileName, projectId);
    }

    /// <summary>Soft-deletes a project (its repositories ride along via the soft-delete marker).</summary>
    public async Task SoftDeleteProjectAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This project no longer exists.");

        // Deleting is stricter than managing: an assigned team does the work on a
        // project, it doesn't get to end it. See .design/teams-and-visibility.md.
        await _access.EnsureCanDeleteAsync(project.CreatedByUserId, ct);

        project.DeletedAt = DateTime.UtcNow;
        project.UpdatedAt = project.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted project {ProjectId}.", id);
    }

    /// <summary>
    /// Validates the input and returns the normalised name/country/repos. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string? Country, IReadOnlyList<ProjectRepositoryInput> Repos)> ValidateAsync(
        ProjectInput input, int? existingId, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the project a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            // Per-org name uniqueness among active rows (the DB enforces it too,
            // now via a case-insensitive lower(name) index — see #432); we
            // pre-check for a friendly inline error rather than a 500. Org-scoping
            // comes from the ambient EF query filter on OeProjects, so no explicit
            // organization_id predicate is needed here.
            var clash = await _db.OeProjects
                .AsNoTracking()
                .AnyAsync(c => c.DeletedAt == null
                               && c.Id != (existingId ?? 0)
                               && c.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another project already uses this name.";
            }
        }

        // Required: builds compile against this localisation's base symbols, and
        // there's no org-wide fallback (the base app varies by country — some
        // localisations ship extra regulatory features).
        string? country = null;
        if (string.IsNullOrWhiteSpace(input.DefaultArtifactCountry))
        {
            errors["DefaultArtifactCountry"] = "Pick the country code of the Microsoft base to compile against, e.g. 'dk' or 'w1'.";
        }
        else
        {
            country = input.DefaultArtifactCountry.Trim().ToLowerInvariant();
            if (!CountryRegex.IsMatch(country))
            {
                errors["DefaultArtifactCountry"] = "Use a BC country code like 'dk' or 'w1'.";
            }
        }

        var repos = input.Repositories ?? Array.Empty<ProjectRepositoryInput>();
        var normalised = new List<ProjectRepositoryInput>(repos.Count);
        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var url = (repo.Url ?? string.Empty).Trim();
            var display = (repo.DisplayName ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                errors[$"Repositories[{i}].Url"] = "Enter the repository URL.";
            }
            else if (!IsValidProviderUrl(repo.Provider, url))
            {
                errors[$"Repositories[{i}].Url"] = repo.Provider == RepositoryProvider.AzureDevOps
                    ? "Use an https Azure DevOps URL (dev.azure.com or *.visualstudio.com)."
                    : "Use an https github.com URL.";
            }
            // Default the display name to the last URL segment when the admin leaves it blank.
            if (display.Length == 0 && url.Length > 0)
            {
                display = url.TrimEnd('/').Split('/').LastOrDefault()?.Replace(".git", "") ?? url;
            }
            normalised.Add(new ProjectRepositoryInput(repo.Provider, url, display));
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return (name, country, normalised);
    }

    /// <summary>True when <paramref name="url"/> is an https URL on a host the provider serves.</summary>
    private static bool IsValidProviderUrl(RepositoryProvider provider, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return provider switch
        {
            RepositoryProvider.AzureDevOps =>
                host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal),
            RepositoryProvider.GitHub =>
                host == "github.com" || host == "www.github.com",
            _ => false,
        };
    }

    /// <summary>
    /// Saves, turning the name-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. The pre-check reads before this writes, so two
    /// concurrent saves can leave one to be caught by the case-insensitive unique
    /// index — and that has to read as an inline message, not a 500. See #702.
    /// </summary>
    private async Task SaveTranslatingNameClashAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Validation("Name", "Another project already uses this name.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static readonly System.Text.RegularExpressions.Regex CountryRegex =
        new("^[a-z0-9]{2,10}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── MCP id resolvers ────────────────────────────────────────────────
    //
    // Relocated from the MCP tool classes so the visibility fence has one
    // home per entity: a rule added here reaches the agent surface too.
    // They throw McpException because their refusal copy is written for an
    // agent; nothing on the web surface calls them.
    // See .design/teams-and-visibility.md.

    /// <summary>
    /// Resolves a project by name or id for an MCP caller, applying the
    /// visibility fence in the same query. A project the caller cannot see
    /// answers "does not exist", the same as an id in another org.
    /// </summary>
    public async Task<int> ResolveProjectAsync(string projectNameOrId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        if (int.TryParse(projectNameOrId, out var asId))
        {
            var exists = await _db.OeProjects.AsNoTracking()
                .Where(visible)
                .AnyAsync(p => p.Id == asId && p.DeletedAt == null, ct);
            if (!exists) throw new McpException($"Solution {asId} does not exist in this organisation.");
            return asId;
        }
        var name = projectNameOrId.Trim();
        var row = await _db.OeProjects.AsNoTracking()
            .Where(visible)
            .Where(p => p.DeletedAt == null && p.Name.ToLower() == name.ToLower())
            .Select(p => new { p.Id })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            throw new McpException($"Solution '{projectNameOrId}' was not found. Call list_solutions to see available solutions.");
        }
        return row.Id;
    }

    /// <summary>Resolves a build that must be ready and have produced a navigable release; returns (projectId, releaseId).</summary>
    public async Task<(int ProjectId, int ReleaseId)> ResolveReadyBuildAsync(int buildId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => _db.OeProjects.Where(visible).Any(p => p.Id == b.ProjectId))
            .Where(b => b.Id == buildId)
            .Select(b => new { b.ProjectId, b.Status, b.ReleaseId })
            .FirstOrDefaultAsync(ct)
            ?? throw new McpException($"Build {buildId} was not found in this organisation.");
        if (build.Status != ProjectBuildStatus.Ready || build.ReleaseId is null)
        {
            throw new McpException($"Build {buildId} can't be compared — only 'ready' builds that produced a release can be diffed.");
        }
        return (build.ProjectId, build.ReleaseId.Value);
    }

}

/// <summary>Form-post shape for a project and its repositories. The repo list is owned wholesale by the editor.</summary>
public sealed record ProjectInput(
    string Name,
    string? DefaultArtifactCountry,
    IReadOnlyList<ProjectRepositoryInput> Repositories);

/// <summary>One repository row from the project editor.</summary>
public sealed record ProjectRepositoryInput(
    RepositoryProvider Provider,
    string Url,
    string DisplayName);

/// <summary>A project's access setting: how visible it is, and which teams are assigned to it.</summary>
public sealed record ProjectAccessSettings(ProjectVisibility Visibility, IReadOnlyList<int> TeamIds);

/// <summary>A release produced by one project's builds — the project detail page's build-history row.</summary>
public sealed record ProjectReleaseRow(int Id, string Label, string Status, string? BcVersion, DateTime ImportedAt, DateTime? DeletedAt);

/// <summary>One uploaded dependency symbol package, ready to store against a project.</summary>
public sealed record SupplementalSymbolUpload(string FileName, byte[] Content);

/// <summary>A stored supplemental symbol — the admin list row (no blob).</summary>
public sealed record ProjectSymbolRow(int Id, string FileName, int ContentLength, DateTime CreatedAt);
