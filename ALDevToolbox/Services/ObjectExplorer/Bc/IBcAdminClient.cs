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

    /// <summary>
    /// Reads the environment's <em>Microsoft platform-update window</em>
    /// (<c>settings/upgrade</c>) — mirrored as context beside the toolbox's own delivery
    /// slot, never as a source for it. Returns <c>null</c> when the environment has no
    /// window configured (the API answers a literal <c>null</c> body) and when the
    /// environment itself is gone (404), because neither is a fault the caller can act
    /// on differently. Throws <see cref="BcApiException"/> on any other non-success.
    /// </summary>
    /// <summary>
    /// Lists the platform target versions for an environment — which Business Central
    /// release is coming next, whether it has been scheduled, and when. Read-only here.
    /// Returns an empty list when the environment has none.
    /// </summary>
    Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Replaces the environment's Microsoft platform-update window, using the API's
    /// <em>wall-time + time-zone</em> parameter set. The UTC parameter set is deliberately
    /// not used: Microsoft warns it resets the time zone shown in the admin center to the
    /// country default, which silently rewrites a setting the customer may have chosen.
    /// <para>
    /// <paramref name="windowsTimeZoneId"/> must be a Windows id (e.g.
    /// <c>Romance Standard Time</c>) — the only form this endpoint accepts. Business
    /// Central has no "clear the window" operation, so this can set a window but never
    /// remove one.
    /// </para>
    /// </summary>
    Task SetUpdateSettingsAsync(
        string accessToken, string? applicationFamily, string environmentName,
        TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default);
}
