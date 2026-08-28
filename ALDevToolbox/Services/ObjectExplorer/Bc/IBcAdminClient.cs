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

    /// <summary>
    /// Reads one environment by name — the live check a delivery makes just before it
    /// uploads, because a run scheduled hours ago may find the environment upgrading by
    /// the time it starts. Returns <c>null</c> when Business Central no longer has an
    /// environment by that name (a 404). <paramref name="applicationFamily"/> is the
    /// family the API reported for the environment; null falls back to the default.
    /// Note the by-name response omits <c>geoName</c>. Throws
    /// <see cref="BcApiException"/> on any other non-success status.
    /// </summary>
    Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);
}
