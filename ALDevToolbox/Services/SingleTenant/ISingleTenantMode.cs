namespace ALDevToolbox.Services.SingleTenant;

/// <summary>
/// Process-local view of the single-tenant deployment flag. When a company
/// hosts the toolbox internally for one organisation, multi-tenant surfaces
/// — storage quotas, per-tenant snapshots, and self-service org creation at
/// signup — are noise. <c>SINGLE_TENANT_MODE=1</c> hides those surfaces and
/// disables their behaviour (enforcement no-ops, the per-tenant snapshot
/// loop is skipped, the matching endpoints 404).
///
/// <para>
/// The flag is fixed at boot from the environment variable, so unlike
/// <see cref="ALDevToolbox.Services.Mcp.IMcpAvailability"/> there's no DB
/// priming or save-path refresh — a plain immutable singleton. NavMenu and
/// the SiteAdmin pages read it on every render, so keeping it in memory
/// avoids a per-request DB hit. See <c>.design/deployment.md</c>.
/// </para>
/// </summary>
public interface ISingleTenantMode
{
    /// <summary>True when the deployment is configured for a single tenant.</summary>
    bool IsEnabled { get; }
}

/// <summary>
/// Immutable singleton holder for the single-tenant flag. Constructed once
/// at startup from <c>SINGLE_TENANT_MODE</c>.
/// </summary>
public sealed class SingleTenantModeState : ISingleTenantMode
{
    public SingleTenantModeState(bool isEnabled) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }
}
