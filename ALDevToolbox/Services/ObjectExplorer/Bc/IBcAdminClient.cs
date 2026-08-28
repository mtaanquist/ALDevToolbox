namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// HTTP seam over the Business Central <em>Admin Center</em> API — the surface that
/// lists a tenant's environments (tenant-scoped by the token). An interface so the
/// connection orchestration is unit-testable without hitting Microsoft, the same reason
/// <c>IProcessRunner</c> exists for git/alc. See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IBcAdminClient
{
    /// <summary>
    /// Lists the tenant's BC environments. Throws <see cref="BcApiException"/> on a
    /// non-success status, carrying the status code and Microsoft's error detail so the
    /// caller can tell 401 (app not authorized in BC) from 403 (app lacks permission)
    /// and name the right fix. See <see cref="BcConstants.AdminEnvironmentsUrl"/>.
    /// </summary>
    Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default);
}
