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
