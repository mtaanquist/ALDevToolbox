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
    /// primary path for listing a tenant's environments. A denial here is one of two
    /// distinct failures: <b>401</b> = the app isn't on BC's "Authorized Microsoft Entra
    /// apps" list, <b>403</b> = it's listed but lacks permission (missing/unconsented
    /// <c>AdminCenter.ReadWrite.All</c>, or a missing GDAP relationship when acting on a
    /// customer's tenant as a partner). GDAP is not assumed — the same connection serves
    /// the maintainer's own tenant, where no such relationship exists.
    /// </summary>
    public const string AdminEnvironmentsUrl =
        "https://api.businesscentral.dynamics.com/admin/v2.21/applications/businesscentral/environments";

    /// <summary>
    /// The per-environment automation API base, in Microsoft's <em>direct tenant</em>
    /// endpoint form: <c>/v2.0/{tenant}/{environment}/api/...</c>. Format args:
    /// {0} = tenant id, {1} = environment name.
    /// <para>
    /// The tenant segment is not optional in practice. Microsoft also documents a
    /// <em>common endpoint</em> form that omits it (<c>/v2.0/{environment}/api/...</c>)
    /// and resolves the tenant from the token, but that resolution fails for an S2S
    /// application token — it answers 401 with no body — and it cannot express the
    /// partner case at all, where the token is for a customer tenant that isn't the
    /// app's own. Addressing the tenant explicitly works for both. See
    /// <c>.design/saas-delivery.md</c> ("Auth").
    /// </para>
    /// </summary>
    public const string AutomationBaseFormat =
        "https://api.businesscentral.dynamics.com/v2.0/{0}/{1}/api/microsoft/automation/v2.0";
}
