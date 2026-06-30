using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Mcp;

namespace ALDevToolbox.Services.Tools;

/// <summary>
/// Process-local cached view of the SiteAdmin's per-tool toggles. The sidebar
/// and the route-access gate hit this on every render / request — keeping it in
/// memory avoids a DB query in hot paths, the same reason
/// <see cref="IMcpAvailability"/> exists. This covers the <em>site</em> level
/// only; the per-org opt-out rides on the <c>org_disabled_tools</c> auth claim.
/// </summary>
public interface IToolAvailability
{
    /// <summary>
    /// True when <paramref name="key"/> is enabled site-wide. MCP delegates to
    /// <see cref="IMcpAvailability.IsEnabled"/> so its toggle has a single source
    /// of truth; every other tool is enabled unless a SiteAdmin disabled it.
    /// </summary>
    bool IsSiteEnabled(ToolKey key);
}

/// <summary>
/// Singleton holder for the site-wide tool toggles. Primed once at startup from
/// <c>system_settings.disabled_tools</c>; <see cref="SystemSettingsService.SaveAsync"/>
/// pushes new values in after each save so the nav and the route gate see them
/// on the next render / request without a restart and without a per-request DB
/// hit. Mirrors <see cref="McpAvailabilityState"/>.
/// </summary>
public sealed class ToolAvailabilityState : IToolAvailability
{
    private readonly IMcpAvailability _mcp;

    // Replaced wholesale on each Set, so reads never see a half-updated set.
    private volatile HashSet<ToolKey> _siteDisabled = new();

    public ToolAvailabilityState(IMcpAvailability mcp) => _mcp = mcp;

    public bool IsSiteEnabled(ToolKey key) =>
        key == ToolKey.Mcp ? _mcp.IsEnabled : !_siteDisabled.Contains(key);

    /// <summary>
    /// Replaces the cached site-disabled set. Called by startup priming and by
    /// the SiteAdmin save path. <see cref="ToolKey.Mcp"/> in the input is
    /// ignored — MCP is owned by <see cref="IMcpAvailability"/>.
    /// </summary>
    public void Set(IEnumerable<ToolKey> disabled) =>
        _siteDisabled = new HashSet<ToolKey>(disabled.Where(k => k != ToolKey.Mcp));
}
