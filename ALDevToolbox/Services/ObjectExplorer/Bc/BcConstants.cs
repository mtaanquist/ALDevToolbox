namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Fixed Microsoft endpoints and scopes for the Business Central delivery layer.
/// Kept in one place so the OAuth token endpoint, the Admin Center API, and the
/// per-environment automation API are easy to find and bump. All hosts are public
/// and fixed (no user-supplied URLs), so no SSRF guard is needed — just bounded
/// timeouts on the shared HttpClient. See <c>.design/saas-delivery.md</c> ("Auth"
/// and "Environment &amp; company discovery").
/// </summary>
internal static class BcConstants
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient"/> for all BC + Entra calls.</summary>
    public const string HttpClientName = "BusinessCentral";

    /// <summary>The Entra (AAD) login host; the token endpoint is <c>{LoginBaseUrl}/{tenantId}/oauth2/v2.0/token</c>.</summary>
    public const string LoginBaseUrl = "https://login.microsoftonline.com";

    /// <summary>
    /// Client-credentials scope for the token every Business Central call uses (the
    /// <c>.default</c> app-permission scope). One resource scope covers the whole BC
    /// API surface, so this is not per-endpoint — it was called <c>AutomationScope</c>
    /// while the automation API was the only thing being called with it.
    /// </summary>
    public const string TokenScope = "https://api.businesscentral.dynamics.com/.default";

    /// <summary>
    /// The Admin Center API version our endpoints address.
    /// <para>
    /// <b>Pinned deliberately, and kept current on purpose.</b> Microsoft keeps old
    /// versions serving for a long time (v2.15 still answered when this was written), so
    /// nothing breaks by sitting still — which is exactly the failure mode: the previous
    /// value, <c>v2.21</c>, was never a decision at all. The design doc wrote the
    /// endpoint as <c>admin/v2.x</c> and the implementer substituted whatever was current
    /// that week; it then sat eight versions behind for months, below the <c>v2.24</c>
    /// that <c>authorizedAadApps/manageableTenants</c> needs, with nobody aware.
    /// </para>
    /// <para>
    /// So the pin is watched rather than trusted: <c>.github/workflows/bc-api-version.yml</c>
    /// probes Microsoft monthly and opens an issue when a newer version ships. It reads
    /// this constant by name — <b>rename it and the probe stops working</b> (it fails
    /// loudly rather than silently passing). Bumping is a one-line change here; re-run a
    /// Test connection afterwards, since the probe proves a version exists, not that the
    /// response shape is unchanged.
    /// </para>
    /// </summary>
    public const string AdminApiVersion = "v2.29";

    /// <summary>
    /// The Admin Center API environments endpoint (tenant-scoped by the token). The
    /// primary path for listing a tenant's environments. A denial here is one of two
    /// distinct failures: <b>401</b> = the app isn't on BC's "Authorized Microsoft Entra
    /// apps" list, <b>403</b> = it's listed but lacks permission (missing/unconsented
    /// <c>AdminCenter.ReadWrite.All</c>, or a missing GDAP relationship when acting on a
    /// customer's tenant as a partner). GDAP is not assumed — the same connection serves
    /// the maintainer's own tenant, where no such relationship exists.
    /// </summary>
    public const string AdminEnvironmentsUrl =
        $"https://api.businesscentral.dynamics.com/admin/{AdminApiVersion}/applications/businesscentral/environments";

    /// <summary>
    /// The application family used when an environment row doesn't carry one yet (rows
    /// fetched before the family was captured). The API reports it CamelCase
    /// (<c>BusinessCentral</c>) while this route has always been called lowercase; both
    /// resolve, so whatever the API returned is passed through unchanged.
    /// </summary>
    public const string DefaultApplicationFamily = "businesscentral";

    /// <summary>
    /// The by-name environment endpoint, for re-reading one environment's live status
    /// just before a delivery uploads. Unlike <see cref="AdminEnvironmentsUrl"/> the
    /// family is a parameter, because it's whatever the API reported for that
    /// environment rather than something we assume.
    /// </summary>
    public static string AdminEnvironmentUrl(string? applicationFamily, string environmentName)
    {
        var family = string.IsNullOrWhiteSpace(applicationFamily) ? DefaultApplicationFamily : applicationFamily.Trim();
        return $"https://api.businesscentral.dynamics.com/admin/{AdminApiVersion}/applications/"
            + $"{Uri.EscapeDataString(family)}/environments/{Uri.EscapeDataString(environmentName)}";
    }

    /// <summary>
    /// Base of the Admin Center's App Management surface for one environment — the
    /// endpoints that upload and track a per-tenant extension. Shares
    /// <see cref="AdminEnvironmentUrl"/>'s treatment of the family, so a row that predates
    /// the family being captured still resolves.
    /// <para>
    /// The PTE endpoints (<c>pteInstall</c>, <c>scheduledPteOperations</c>,
    /// <c>removeScheduledPteVersion</c>) were introduced in v2.29, so this surface does
    /// not exist below the pinned <see cref="AdminApiVersion"/>.
    /// </para>
    /// </summary>
    public static string AppManagementBaseUrl(string? applicationFamily, string environmentName) =>
        $"{AdminEnvironmentUrl(applicationFamily, environmentName)}/apps";

    /// <summary>
    /// An environment's platform target versions — which Business Central release it is
    /// getting next, and when.
    /// </summary>
    public static string EnvironmentUpdatesUrl(string? applicationFamily, string environmentName) =>
        $"{AdminEnvironmentUrl(applicationFamily, environmentName)}/updates";

    /// <summary>
    /// An environment's update-settings endpoint — <em>Microsoft's</em> platform-update
    /// window for that environment, which is not the toolbox's delivery slot. Read with
    /// GET, replaced with PUT.
    /// </summary>
    public static string EnvironmentUpdateSettingsUrl(string? applicationFamily, string environmentName) =>
        $"{AdminEnvironmentUrl(applicationFamily, environmentName)}/settings/upgrade";
}
