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
    /// non-success status — a 404 included, because on this tenant-wide route it means the
    /// call went somewhere wrong rather than that the tenant has no environments — carrying
    /// the status code and Microsoft's error detail so the caller can tell 401 (app not
    /// authorized in BC) from 403 (app lacks permission) and name the right fix.
    /// See <see cref="BcConstants.AdminEnvironmentsUrl"/>.
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
    /// Lists the platform target versions for an environment — which Business Central
    /// release is coming next, whether it has been scheduled, and when. Read-only here.
    /// Returns an empty list when the environment has none, and when the environment
    /// itself is gone (404): a removed environment has nothing on offer, which is the
    /// same reading <see cref="GetEnvironmentAsync"/> gives a 404. Throws
    /// <see cref="BcApiException"/> on any other non-success status.
    /// </summary>
    Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// The operations Business Central has recorded against one environment, in the
    /// order it returns them. An environment that is gone answers with an empty list,
    /// as <see cref="ListEnvironmentUpdatesAsync"/> does. Throws
    /// <see cref="BcApiException"/> on any other non-success status.
    /// </summary>
    /// <summary>
    /// The database size of every environment in the tenant, by environment name, and
    /// what the tenant is allowed in total. The allowance is the <em>tenant's</em> - all
    /// its environments share it - which is why this is one read and not one per
    /// environment. An environment whose size Business Central could not work out (it
    /// reports -1) is left out. Throws <see cref="BcApiException"/> on a non-success status.
    /// </summary>
    Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default);

    Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Who is signed in to the environment right now - people in the web client, web
    /// service callers, and Business Central's own background sessions. Live every time:
    /// nothing about this answer is worth caching, and nothing about it is stored.
    /// An environment that is gone answers with an empty list, as
    /// <see cref="ListEnvironmentOperationsAsync"/> does. Throws
    /// <see cref="BcApiException"/> on any other non-success status.
    /// </summary>
    Task<IReadOnlyList<BcSession>> ListSessionsAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Ends one session on the environment. A write to the customer's tenant, and one
    /// somebody feels immediately: Business Central drops the session where it stands and
    /// whatever that person had not saved is lost. Callers confirm by name first.
    /// <para>
    /// A session that has already ended answers 404, which comes back as a
    /// <see cref="BcApiException"/> whose message says so - Microsoft documents no error
    /// codes of its own for this endpoint, so the shared settings wording carries the rest.
    /// </para>
    /// </summary>
    Task CancelSessionAsync(
        string accessToken, string? applicationFamily, string environmentName, int sessionId,
        CancellationToken ct = default);

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

    /// <summary>
    /// Brings back an environment the customer deleted, while Business Central is still
    /// keeping it. A write to the customer's tenant, and the one that can't be repeated:
    /// once Microsoft has hard-deleted the environment there is nothing to recover.
    /// <para>
    /// Business Central answers with a scheduled operation rather than a finished one, so
    /// the environment moves through <c>Recovering</c> before it is <c>Active</c> again;
    /// callers re-read it rather than assuming. A refusal comes back as a
    /// <see cref="BcApiException"/> whose message is already a sentence — an environment
    /// that is already being recovered and one whose state forbids it are told apart.
    /// </para>
    /// </summary>
    Task RecoverEnvironmentAsync(
        string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default);

    /// <summary>
    /// Makes a copy of <paramref name="sourceEnvironmentName"/> under
    /// <paramref name="newEnvironmentName"/>. A write to the customer's tenant that adds
    /// an environment to it, so it counts against their allowance and, for a production
    /// copy, their licences.
    /// <para>
    /// Business Central answers with a scheduled operation and takes its time: the new
    /// environment appears in the environments list as <c>Preparing</c> and turns
    /// <c>Active</c> when it is ready. Nothing here waits for that — the returned
    /// <see cref="BcEnvironmentCopy"/> is the operation, and the environment's own
    /// Operations tab is where it is watched.
    /// </para>
    /// <para>
    /// <paramref name="newEnvironmentType"/> is a <see cref="BcEnvironmentTypes"/> value.
    /// A refusal comes back as a <see cref="BcApiException"/> whose message is already a
    /// sentence; the codes Microsoft documents for this endpoint are told apart.
    /// </para>
    /// </summary>
    Task<BcEnvironmentCopy> CopyEnvironmentAsync(
        string accessToken, string? applicationFamily, string sourceEnvironmentName,
        string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default);

    /// <summary>
    /// Reads the environment's <em>Microsoft platform-update window</em>
    /// (<c>settings/upgrade</c>) — mirrored as context beside the workbench's own delivery
    /// slot, never as a source for it. Returns <c>null</c> when the environment has no
    /// window configured (the API answers a literal <c>null</c> body) and when the
    /// environment itself is gone (404), because neither is a fault the caller can act
    /// on differently. Throws <see cref="BcApiException"/> on any other non-success.
    /// </summary>
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
