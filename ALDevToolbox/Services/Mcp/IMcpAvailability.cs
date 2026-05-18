using ALDevToolbox.Services;

namespace ALDevToolbox.Services.Mcp;

/// <summary>
/// Process-local cached view of the SiteAdmin's MCP toggle. NavMenu and the
/// MCP page hit this on every render — keeping it in memory avoids issuing
/// a DB query inside <c>OnInitializedAsync</c>, where a connection in
/// flight can collide with the request scope being torn down (especially
/// during <c>UseStatusCodePagesWithReExecute</c>'s /not-found re-execute).
/// </summary>
public interface IMcpAvailability
{
    /// <summary>True when the SiteAdmin has enabled MCP under /site-admin/settings.</summary>
    bool IsEnabled { get; }
}

/// <summary>
/// Singleton holder for the MCP toggle. Primed once at startup from
/// <c>system_settings.mcp_enabled</c>; <see cref="SystemSettingsService.SaveAsync"/>
/// pushes new values in after each save so the link visibility and
/// endpoint kill-switch see them on the next render / request without a
/// restart and without a per-request DB hit.
/// </summary>
public sealed class McpAvailabilityState : IMcpAvailability
{
    private volatile bool _isEnabled;

    public bool IsEnabled => _isEnabled;

    /// <summary>Replaces the cached value. Called by startup priming and by the SiteAdmin save path.</summary>
    public void Set(bool value) => _isEnabled = value;
}
