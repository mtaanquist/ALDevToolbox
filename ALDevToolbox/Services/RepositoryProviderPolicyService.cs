using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Owns which Git hosting providers an organisation allows project repositories
/// on. Reads answer from the cached snapshot
/// <see cref="OrganizationConfigService"/> holds, so the pickers that ask on
/// every render add no DB round-trip; the save invalidates that cache.
/// </summary>
public class RepositoryProviderPolicyService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<RepositoryProviderPolicyService> _logger;

    public RepositoryProviderPolicyService(
        AppDbContext db,
        IOrganizationContext orgContext,
        OrganizationConfigService config,
        ILogger<RepositoryProviderPolicyService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _config = config;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns the tracked <see cref="Domain.Entities.OrganizationSettings"/> row for
    /// <paramref name="orgId"/>, inserting a fresh one (added to the change
    /// tracker) when none exists yet. Runs under the normal tenant query filter
    /// — the caller already holds the acting org id from
    /// <see cref="RequireOrganizationId"/>, so there is no cross-org read here.
    /// </summary>
    private async Task<Domain.Entities.OrganizationSettings> GetOrCreateSettingsAsync(int orgId, CancellationToken ct)
    {
        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new Domain.Entities.OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        return row;
    }

    /// <summary>
    /// The set of providers an org with no explicit configuration allows. An
    /// unconfigured org permits both, so the add-repo picker and the per-user token
    /// page work before an admin narrows the list.
    /// </summary>
    private static readonly IReadOnlyList<RepositoryProvider> AllProviders =
        new[] { RepositoryProvider.GitHub, RepositoryProvider.AzureDevOps };

    /// <summary>
    /// The Git hosting providers this org allows project repositories on. An empty
    /// stored list (a never-configured org) resolves to <see cref="AllProviders"/>
    /// so the tool isn't broken before the setting is touched. Reads the cached
    /// <see cref="OrganizationConfig"/>, so it adds no DB round-trip.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryProvider>> GetAllowedProvidersAsync(CancellationToken ct = default)
    {
        var config = await _config.GetCurrentAsync(ct);
        return ParseAllowedProviders(config.Settings.AllowedRepositoryProviders);
    }

    /// <summary>True when <paramref name="provider"/> is permitted for the current org.</summary>
    public async Task<bool> IsProviderAllowedAsync(RepositoryProvider provider, CancellationToken ct = default)
        => (await GetAllowedProvidersAsync(ct)).Contains(provider);

    /// <summary>
    /// Persists which providers the org allows. At least one is required — an empty
    /// selection throws <see cref="PlanValidationException"/> (field key
    /// <c>AllowedRepositoryProviders</c>) so the form renders the error inline.
    /// Members store their own per-provider tokens under their account.
    /// </summary>
    public async Task SaveAllowedProvidersAsync(IReadOnlyList<RepositoryProvider> providers, CancellationToken ct = default)
    {
        if (providers is null || providers.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AllowedRepositoryProviders"] = "Pick at least one source-control provider.",
            });
        }

        var orgId = RequireOrganizationId();
        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.AllowedRepositoryProviders = providers.Distinct().Select(p => p.ToDiscriminator()).ToList();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated allowed repository providers for org {OrgId} ({Providers}).",
            orgId, string.Join(", ", row.AllowedRepositoryProviders));
    }

    /// <summary>Maps the stored discriminators back to providers; empty means all allowed.</summary>
    private static IReadOnlyList<RepositoryProvider> ParseAllowedProviders(List<string> stored)
    {
        if (stored is null || stored.Count == 0) return AllProviders;
        var parsed = stored
            .Select(RepositoryProviders.FromDiscriminator)
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .Distinct()
            .ToList();
        return parsed.Count == 0 ? AllProviders : parsed;
    }
}
