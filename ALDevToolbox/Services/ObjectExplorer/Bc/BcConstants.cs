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

    /// <summary>Client-credentials scope for the BC APIs (the <c>.default</c> app-permission scope).</summary>
    public const string AutomationScope = "https://api.businesscentral.dynamics.com/.default";

    /// <summary>
    /// The Admin Center API environments endpoint (tenant-scoped by the token). The
    /// primary path for listing a customer's environments; requires the maintainer's
    /// GDAP relationship — a 401/403 here means GDAP is missing/insufficient.
    /// </summary>
    public const string AdminEnvironmentsUrl =
        "https://api.businesscentral.dynamics.com/admin/v2.21/applications/businesscentral/environments";

    /// <summary>
    /// The per-environment automation API base; keys on the <em>environment name</em>,
    /// not the tenant id (the tenant id is what the token is for). Format args:
    /// {0} = environment name.
    /// </summary>
    public const string AutomationBaseFormat =
        "https://api.businesscentral.dynamics.com/v2.0/{0}/api/microsoft/automation/v2.0";
}
