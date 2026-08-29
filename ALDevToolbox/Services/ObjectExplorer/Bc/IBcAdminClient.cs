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

    /// <summary>
    /// The time zones Business Central accepts for an update window. Tenant-wide, so it
    /// takes no environment. Its ids are the only values
    /// <see cref="SetUpdateSettingsAsync"/> will take.
    /// </summary>
    Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Sets how often Marketplace apps on the environment are updated. A write to the
    /// customer's tenant. <paramref name="cadence"/> is a <see cref="BcAppUpdateCadence"/>
    /// value.
    /// </summary>
    Task SetAppUpdateCadenceAsync(
        string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default);

    /// <summary>Whether people with only a Microsoft 365 licence may sign in to the environment.</summary>
    Task<bool?> GetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Turns Microsoft 365 licence access on or off for the environment — it changes who
    /// can sign in to the customer's tenant, so callers confirm first.
    /// </summary>
    Task SetM365AccessAsync(
        string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Selects the platform version the environment updates to next, and optionally when
    /// it runs. <b>This reschedules a customer's Business Central upgrade</b>, so callers
    /// confirm against the environment by name first. Only a version the updates read
    /// reports as available can be selected.
    /// <para>
    /// <paramref name="selectedDateTime"/> sets the moment Microsoft starts the update;
    /// it must be no later than the version's latest selectable date. Leave it null to
    /// pick the version without moving its date.
    /// </para>
    /// <para>
    /// <paramref name="ignoreUpdateWindow"/> lets the update start outside the
    /// environment's update window — only ever set by "update now" (issue #657), because
    /// it takes away the customer's protection against an upgrade in working hours.
    /// </para>
    /// </summary>
    Task SelectTargetVersionAsync(
        string accessToken, string? applicationFamily, string environmentName,
        string targetVersion, string? targetVersionType,
        DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null,
        CancellationToken ct = default);

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
