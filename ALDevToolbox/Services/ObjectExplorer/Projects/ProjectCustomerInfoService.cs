using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// What support looks up about a customer: where their Business Central runs, how to
/// get in, who to call. Reading follows the solution's visibility; writing is for the
/// people who manage it. See <c>.design/solution-customer-info.md</c>.
/// </summary>
public sealed class ProjectCustomerInfoService
{
    public const int VersionMaxLength = 50;
    public const int ClientUrlMaxLength = 500;
    public const int VoiceAccountNumberMaxLength = 30;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<ProjectCustomerInfoService> _logger;

    public ProjectCustomerInfoService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ILogger<ProjectCustomerInfoService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// The basics for one solution, or null when it does not exist in this organisation.
    /// Throws <see cref="ProjectAccessDeniedException"/> for a Private solution the
    /// caller has no grant on.
    /// </summary>
    public async Task<CustomerBasics?> GetBasicsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjects
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new CustomerBasics(
                p.HostingType, p.BcVersion, p.LicenseType, p.UserExperience,
                p.ClientUrl, p.VoiceAccountNumber, p.BcTenantId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Saves the basics. Every field is optional. Refuses an on-premises hosting type
    /// while Business Central online reports environments for the solution: the
    /// surfaces that show them disappear for an on-premises solution, and live ones
    /// must not be hidden.
    /// <para>
    /// The tenant id is the customer's Microsoft tenant, which an on-premises customer
    /// has too. For an online solution the Business Central tab owns it (changing it
    /// there resets the connection), so it is only written from here when the solution
    /// is on-premises and that tab is gone.
    /// </para>
    /// </summary>
    public async Task SaveBasicsAsync(int projectId, CustomerBasicsInput input, CancellationToken ct = default)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Saving customer information needs an authenticated request.");

        var errors = new Dictionary<string, string>();
        var version = Clean(input.BcVersion);
        var url = Clean(input.ClientUrl);
        var voice = Clean(input.VoiceAccountNumber);

        if (version?.Length > VersionMaxLength)
            errors["BcVersion"] = $"Keep the version under {VersionMaxLength} characters, e.g. 'BC 25.3' or 'NAV 2018 CU12'.";
        if (url is not null)
        {
            if (url.Length > ClientUrlMaxLength)
                errors["ClientUrl"] = $"Keep the address under {ClientUrlMaxLength} characters.";
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                     || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                errors["ClientUrl"] = "Enter the full address, starting with https:// or http://.";
        }
        if (voice?.Length > VoiceAccountNumberMaxLength)
            errors["VoiceAccountNumber"] = $"Keep the Voice account number under {VoiceAccountNumberMaxLength} characters.";

        var project = await _db.OeProjects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["HostingType"] = "This solution no longer exists." });
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);

        var onPremises = input.HostingType is not (null or ProjectHostingType.MicrosoftCloud);
        Guid? tenantId = null;
        if (onPremises)
        {
            var environments = await _db.OeProjectEnvironments.CountAsync(e => e.ProjectId == project.Id, ct);
            if (environments > 0)
            {
                errors["HostingType"] = environments == 1
                    ? "Business Central online reports an environment for this solution, so it can't be on-premises."
                    : $"Business Central online reports {environments} environments for this solution, so it can't be on-premises.";
            }

            if (Clean(input.TenantId) is { } typed)
            {
                if (Guid.TryParse(typed, out var parsed)) tenantId = parsed;
                else errors["TenantId"] = "A tenant ID looks like 11111111-2222-3333-4444-555555555555.";
            }
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.HostingType = input.HostingType;
        project.BcVersion = version;
        project.LicenseType = input.LicenseType;
        project.UserExperience = input.UserExperience;
        project.ClientUrl = url;
        project.VoiceAccountNumber = voice;
        if (onPremises) project.BcTenantId = tenantId;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved customer basics for project {ProjectId}; hosting {HostingType}.", project.Id, project.HostingType);
    }

    /// <summary>
    /// Everything the Customer tab shows, read one after the other. The tab's sections
    /// are siblings on one circuit and so share this service's <c>DbContext</c>, which
    /// allows one operation at a time - letting each section read for itself on first
    /// render is how "a second operation was started" happens (#741).
    /// </summary>
    public async Task<CustomerInfoSnapshot?> GetAllAsync(int projectId, CancellationToken ct = default)
    {
        var basics = await GetBasicsAsync(projectId, ct);
        if (basics is null) return null;
        return new CustomerInfoSnapshot(
            basics,
            await GetNotesAsync(projectId, ct) ?? new CustomerNotes(null, null, null),
            await ListContactsAsync(projectId, ct),
            await ListPeopleAsync(projectId, ct),
            await ListIntegrationsAsync(projectId, ct));
    }

    /// <summary>
    /// Hosting and version for the Solutions list's columns, by solution id. The caller
    /// passes the ids of rows it is already showing in full - never a locked row's, whose
    /// name is all its viewer may see.
    /// </summary>
    public async Task<Dictionary<int, CustomerListFacts>> ListFactsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return new();
        return await _db.OeProjects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => new { p.Id, p.HostingType, p.BcVersion })
            .ToDictionaryAsync(p => p.Id, p => new CustomerListFacts(p.HostingType, p.BcVersion), ct);
    }

    // ── Getting in, and notes ───────────────────────────────────────────

    public const int NotesMaxLength = 4000;

    public async Task<CustomerNotes?> GetNotesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new CustomerNotes(p.AccessDescription, p.HostingNotes, p.KnowledgeNotes))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Plain text, deliberately not encrypted - see the design doc. Blank clears.</summary>
    public async Task SaveNotesAsync(int projectId, CustomerNotes input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        void Check(string field, string? value, string what)
        {
            if (Clean(value)?.Length > NotesMaxLength)
                errors[field] = $"Keep {what} under {NotesMaxLength} characters.";
        }
        Check("AccessDescription", input.AccessDescription, "how to get in");
        Check("HostingNotes", input.HostingNotes, "the hosting notes");
        Check("KnowledgeNotes", input.KnowledgeNotes, "the notes");

        var project = await LoadManagedProjectAsync(projectId, ct);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.AccessDescription = Clean(input.AccessDescription);
        project.HostingNotes = Clean(input.HostingNotes);
        project.KnowledgeNotes = Clean(input.KnowledgeNotes);
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Saved customer notes for project {ProjectId}.", projectId);
    }

    // ── Contacts ────────────────────────────────────────────────────────

    /// <summary>The customer's own people first, then by name.</summary>
    public async Task<List<CustomerContact>> ListContactsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectContacts.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .Select(c => new CustomerContact(c.Id, c.Type, c.Name, c.Company, c.Email, c.Phone))
            .ToListAsync(ct);
        return rows.OrderBy(c => c.Type).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds a contact, or changes one when <paramref name="contactId"/> is given.</summary>
    public async Task SaveContactAsync(int projectId, int? contactId, CustomerContactInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = Clean(input.Name);
        var company = Clean(input.Company);
        var email = Clean(input.Email);
        var phone = Clean(input.Phone);

        if (name is null) errors["Name"] = "Enter the contact's name.";
        else if (name.Length > 100) errors["Name"] = "Keep the name under 100 characters.";
        if (company?.Length > 100) errors["Company"] = "Keep the company under 100 characters.";
        if (email is not null && (email.Length > 200 || !email.Contains('@') || email.Contains(' ')))
            errors["Email"] = "Enter an email address like name@cronus.example.";
        if (phone?.Length > 50) errors["Phone"] = "Keep the phone number under 50 characters.";
        if (name is not null && email is null && phone is null)
            errors["Email"] = "Enter an email address or a phone number, so there is a way to reach them.";

        var project = await LoadManagedProjectAsync(projectId, ct);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectContact row;
        if (contactId is { } id)
        {
            row = await _db.OeProjectContacts.FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId, ct)
                ?? throw Gone("Name", "That contact no longer exists.");
        }
        else
        {
            row = new OeProjectContact { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectContacts.Add(row);
        }

        row.Type = input.Type;
        row.Name = name!;
        row.Company = company;
        row.Email = email;
        row.Phone = phone;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteContactAsync(int projectId, int contactId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectContacts.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── Who knows this customer ─────────────────────────────────────────

    public async Task<List<CustomerPerson>> ListPeopleAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectPeople.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => new CustomerPerson(p.Id, p.UserId, p.User!.DisplayName, p.User.Email, p.Role, p.Areas))
            .ToListAsync(ct);
        return rows.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The organisation's active users, for the picker.</summary>
    public async Task<List<CustomerPersonOption>> ListAssignableUsersAsync(CancellationToken ct = default)
    {
        var rows = await _db.Users.AsNoTracking()
            .Where(u => u.Status == Domain.Entities.UserStatus.Active)
            .Select(u => new CustomerPersonOption(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
        return rows.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds one of our people, or changes their role and areas. One row per person.</summary>
    public async Task SavePersonAsync(int projectId, int? personId, CustomerPersonInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var areas = Clean(input.Areas);
        if (areas?.Length > 250) errors["Areas"] = "Keep the areas under 250 characters.";

        var project = await LoadManagedProjectAsync(projectId, ct);

        // The query filter keeps this to the caller's own organisation.
        if (input.UserId is not { } userId || !await _db.Users.AnyAsync(u => u.Id == userId, ct))
            errors["UserId"] = "Choose one of your colleagues.";
        else if (await _db.OeProjectPeople.AnyAsync(p => p.ProjectId == projectId && p.UserId == userId && p.Id != personId, ct))
            errors["UserId"] = "They are already on the list. Change their role or areas there.";

        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectPerson row;
        if (personId is { } id)
        {
            row = await _db.OeProjectPeople.FirstOrDefaultAsync(p => p.Id == id && p.ProjectId == projectId, ct)
                ?? throw Gone("UserId", "That entry no longer exists.");
        }
        else
        {
            row = new OeProjectPerson { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectPeople.Add(row);
        }

        row.UserId = input.UserId!.Value;
        row.Role = input.Role;
        row.Areas = areas;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeletePersonAsync(int projectId, int personId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectPeople.FirstOrDefaultAsync(p => p.Id == personId && p.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectPeople.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── Integrations ────────────────────────────────────────────────────

    public async Task<List<CustomerIntegration>> ListIntegrationsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectIntegrations.AsNoTracking()
            .Where(i => i.ProjectId == projectId)
            .Select(i => new CustomerIntegration(i.Id, i.Name, i.Direction))
            .ToListAsync(ct);
        return rows.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SaveIntegrationAsync(int projectId, int? integrationId, CustomerIntegrationInput input, CancellationToken ct = default)
    {
        var name = Clean(input.Name);
        var project = await LoadManagedProjectAsync(projectId, ct);
        if (name is null) throw Gone("Name", "Enter what Business Central is integrated with.");
        if (name.Length > 100) throw Gone("Name", "Keep the name under 100 characters.");

        OeProjectIntegration row;
        if (integrationId is { } id)
        {
            row = await _db.OeProjectIntegrations.FirstOrDefaultAsync(i => i.Id == id && i.ProjectId == projectId, ct)
                ?? throw Gone("Name", "That integration no longer exists.");
        }
        else
        {
            row = new OeProjectIntegration { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectIntegrations.Add(row);
        }

        row.Name = name;
        row.Direction = input.Direction;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteIntegrationAsync(int projectId, int integrationId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectIntegrations.FirstOrDefaultAsync(i => i.Id == integrationId && i.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectIntegrations.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The tracked solution, once the caller is known to manage it.</summary>
    private async Task<OeProject> LoadManagedProjectAsync(int projectId, CancellationToken ct)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing customer information needs an authenticated request.");
        var project = await _db.OeProjects.FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Gone("Name", "This solution no longer exists.");
        await _access.EnsureCanManageAsync(project.Id, project.CreatedByUserId, ct);
        return project;
    }

    private static PlanValidationException Gone(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The basics as read. <paramref name="TenantId"/> is the Business Central connection's, shown for reference.</summary>
public sealed record CustomerBasics(
    ProjectHostingType? HostingType,
    string? BcVersion,
    ProjectLicenseType? LicenseType,
    ProjectUserExperience? UserExperience,
    string? ClientUrl,
    string? VoiceAccountNumber,
    Guid? TenantId)
{
    public bool IsOnPremises => HostingType is not (null or ProjectHostingType.MicrosoftCloud);

    /// <summary>True when nobody has entered anything yet, which is the tab's first-run state.</summary>
    public bool IsEmpty => HostingType is null && BcVersion is null && LicenseType is null
        && UserExperience is null && ClientUrl is null && VoiceAccountNumber is null;
}

public sealed record CustomerBasicsInput(
    ProjectHostingType? HostingType,
    string? BcVersion,
    ProjectLicenseType? LicenseType,
    ProjectUserExperience? UserExperience,
    string? ClientUrl,
    string? VoiceAccountNumber,
    string? TenantId = null);

public sealed record CustomerNotes(string? AccessDescription, string? HostingNotes, string? KnowledgeNotes)
{
    public bool IsEmpty => AccessDescription is null && HostingNotes is null && KnowledgeNotes is null;
}

public sealed record CustomerContact(int Id, ProjectContactType Type, string Name, string? Company, string? Email, string? Phone);
public sealed record CustomerContactInput(ProjectContactType Type, string? Name, string? Company, string? Email, string? Phone);

public sealed record CustomerPerson(int Id, int UserId, string Name, string Email, ProjectPersonRole Role, string? Areas);
public sealed record CustomerPersonOption(int UserId, string Name, string Email);
public sealed record CustomerPersonInput(int? UserId, ProjectPersonRole Role, string? Areas);

public sealed record CustomerIntegration(int Id, string Name, ProjectIntegrationDirection Direction);
public sealed record CustomerIntegrationInput(string? Name, ProjectIntegrationDirection Direction);

/// <summary>The whole Customer tab in one read. See <see cref="ProjectCustomerInfoService.GetAllAsync"/>.</summary>
public sealed record CustomerInfoSnapshot(
    CustomerBasics Basics,
    CustomerNotes Notes,
    List<CustomerContact> Contacts,
    List<CustomerPerson> People,
    List<CustomerIntegration> Integrations);

public sealed record CustomerListFacts(ProjectHostingType? HostingType, string? BcVersion);
