using System.Collections.Concurrent;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// A short-lived in-memory cache of environment-panel reads, so opening the same
/// environment repeatedly in one sitting costs Business Central one set of calls rather
/// than one per open. Registered as a <strong>singleton</strong> so the cache is shared
/// across requests, like <see cref="BcTokenService"/>; it holds no scoped
/// <c>DbContext</c> and no secrets, only what the Admin Center already told us.
/// <para>
/// <b>Why fifteen minutes.</b> Nearly all repeat traffic is one consultant expanding an
/// environment, collapsing it, opening another and coming back — a window this short
/// collapses a whole working session into one fetch while leaving the panel honest
/// enough that nobody has to think about staleness. A longer TTL would only additionally
/// catch the next person tomorrow, for much more staleness. See
/// <c>.design/saas-delivery.md</c>.
/// </para>
/// <para>
/// <b>The cache is not an access check.</b> Entries are keyed by project and environment
/// id only, so the caller must complete its organisation and access checks
/// <em>before</em> consulting it — see <see cref="ProjectConnectionService.GetEnvironmentPanelAsync"/>,
/// which gates first and reads the cache second.
/// </para>
/// </summary>
public sealed class BcPanelCache
{
    /// <summary>How long a panel read stays usable. Deliberately a constant, not a setting.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<(int ProjectId, int EnvironmentId), BcEnvironmentPanel> _cache = new();
    private readonly TimeProvider _clock;

    public BcPanelCache(TimeProvider clock) => _clock = clock;

    /// <summary>
    /// The cached panel for this environment, or null when there is none or it has aged
    /// out. A panel that reported section errors is cached like any other — it is what
    /// Business Central said, and a permission that gets fixed is picked up by Refresh,
    /// which bypasses the cache.
    /// </summary>
    public BcEnvironmentPanel? Get(int projectId, int environmentId)
    {
        if (!_cache.TryGetValue((projectId, environmentId), out var panel))
        {
            return null;
        }

        if (_clock.GetUtcNow().UtcDateTime - panel.FetchedAtUtc >= Ttl)
        {
            _cache.TryRemove((projectId, environmentId), out _);
            return null;
        }

        return panel;
    }

    /// <summary>Stores a freshly-read panel.</summary>
    public void Set(int projectId, int environmentId, BcEnvironmentPanel panel)
        => _cache[(projectId, environmentId)] = panel;

    /// <summary>
    /// Drops this environment's entry. Called after any write of ours that changes what
    /// the panel shows, so a consultant never sees a stale answer as a result of
    /// something they just did here.
    /// </summary>
    public void Invalidate(int projectId, int environmentId)
        => _cache.TryRemove((projectId, environmentId), out _);

    /// <summary>
    /// Drops every environment under a project — for credential changes and for writes
    /// that name an environment by name rather than by id.
    /// </summary>
    public void InvalidateProject(int projectId)
    {
        foreach (var key in _cache.Keys.Where(k => k.ProjectId == projectId))
        {
            _cache.TryRemove(key, out _);
        }
    }
}
