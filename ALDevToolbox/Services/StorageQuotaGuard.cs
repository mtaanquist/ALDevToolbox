using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.SingleTenant;
using Microsoft.Extensions.Caching.Memory;

namespace ALDevToolbox.Services;

/// <summary>
/// Hard-blocks writes for organisations whose billable database footprint
/// has reached the configured quota. SiteAdmin sessions and the system
/// organisation are exempt. Reads are never blocked.
///
/// Services that mutate tenant-scoped state must call
/// <see cref="EnsureCanWriteAsync"/> as the first step of the operation.
/// The guard caches its decision per organisation for 60 seconds in
/// <see cref="IMemoryCache"/> so the hot generation path doesn't pay for
/// a usage scan on every request.
/// </summary>
public sealed class StorageQuotaGuard
{
    /// <summary>Cache key prefix for memoised usage decisions.</summary>
    private const string CacheKeyPrefix = "storage-quota:";

    /// <summary>How long a guard decision is cached before being recomputed.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly DatabaseUsageService _usage;
    private readonly IOrganizationContext _orgContext;
    private readonly IMemoryCache _cache;
    private readonly ISingleTenantMode _singleTenant;
    private readonly ILogger<StorageQuotaGuard> _logger;

    public StorageQuotaGuard(
        DatabaseUsageService usage,
        IOrganizationContext orgContext,
        IMemoryCache cache,
        ISingleTenantMode singleTenant,
        ILogger<StorageQuotaGuard> logger)
    {
        _usage = usage;
        _orgContext = orgContext;
        _cache = cache;
        _singleTenant = singleTenant;
        _logger = logger;
    }

    /// <summary>
    /// Refuses the current operation when the acting organisation is at or
    /// over its effective storage quota. SiteAdmin and the system org pass
    /// through. Throws <see cref="PlanValidationException"/> with the
    /// <c>storage</c> field key so the form layer can render the message
    /// inline rather than 500-ing.
    /// </summary>
    public async Task EnsureCanWriteAsync(CancellationToken ct)
    {
        // Single-tenant deployments hide quotas entirely — never block a write
        // on a limit the operator can't see or manage.
        if (_singleTenant.IsEnabled) return;
        if (_orgContext.IsSiteAdmin) return;
        if (_orgContext.IsSystemOrganization) return;

        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null) return;

        var decision = await _cache.GetOrCreateAsync(CacheKey(orgId.Value), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var row = await _usage.GetForCurrentOrgAsync(ct);
            if (row is null) return new Decision(false, 0, null);
            // No effective quota set means unlimited — never blocks.
            if (row.EffectiveQuotaMb is null) return new Decision(false, row.BillableBytes, null);
            var quotaBytes = (long)row.EffectiveQuotaMb.Value * 1024L * 1024L;
            return new Decision(row.BillableBytes >= quotaBytes, row.BillableBytes, row.EffectiveQuotaMb);
        });

        if (decision is { IsOverQuota: true })
        {
            _logger.LogWarning(
                "Refused write for org {OrgId}: billable {Billable} bytes ≥ quota {Quota} MB.",
                orgId, decision.BillableBytes, decision.EffectiveQuotaMb);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["storage"] = $"This organisation has used its storage quota ({decision.EffectiveQuotaMb} MB). "
                              + "Ask your SiteAdmin to free space or raise the quota before retrying.",
            });
        }
    }

    /// <summary>
    /// Invalidates the cached decision for an organisation. Call after large
    /// writes (generation, template import, file uploads) so the next check
    /// sees fresh usage instead of a stale 60-second-old answer.
    /// </summary>
    public void Invalidate(int organizationId) => _cache.Remove(CacheKey(organizationId));

    private static string CacheKey(int organizationId) => CacheKeyPrefix + organizationId;

    private sealed record Decision(bool IsOverQuota, long BillableBytes, int? EffectiveQuotaMb);
}
