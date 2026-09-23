using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The read side of the Upgrades page: one row per Business Central environment across
/// every project the caller can see, answered entirely from the <c>bc_next_update_*</c>
/// mirror so a hundred customers list without a hundred live round trips. See
/// <c>.design/saas-delivery.md</c> and issue #657.
///
/// <para>
/// <b>The join is the guard.</b> <see cref="OeProjectEnvironment"/> has no visibility rule
/// of its own — it inherits its project's. Every query here therefore reaches the
/// environments table <em>through</em>
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>, and a future query that lists
/// environments must do the same rather than reading <c>OeProjectEnvironments</c>
/// directly. The org fence (the EF query filter) still sits underneath it.
/// </para>
/// </summary>
public sealed class UpgradeFleetService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly EnvironmentRefreshQueue _refreshQueue;
    private readonly ILogger<UpgradeFleetService> _logger;

    public UpgradeFleetService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        EnvironmentRefreshQueue refreshQueue,
        ILogger<UpgradeFleetService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _refreshQueue = refreshQueue;
        _logger = logger;
    }

    /// <summary>
    /// Every environment of every visible project that Business Central still reports
    /// (<see cref="OeProjectEnvironment.MissingSince"/> null), with the mirrored next
    /// update and whether this caller may act on it.
    ///
    /// <para>A soft-deleted environment is left out by default, because the Upgrades
    /// page exists to move update dates and a deleted environment's cannot be moved.
    /// The Environments page asks for them with
    /// <paramref name="includeSoftDeleted"/>: there the point is the state of every
    /// environment, and "deleted, still restorable" is one of the states worth
    /// seeing.</para>
    ///
    /// <para>The "may act" answer is computed as part of the same query — a subquery
    /// over <see cref="ProjectAccess.UpdateOpsProjectPredicate"/> — rather than a check
    /// per row, so a fleet of a hundred environments costs one round trip.</para>
    ///
    /// <para>Ordered by customer, then Production before everything else, then name:
    /// the sweep runs customer by customer, and Production is the row the upgrade team
    /// is looking for when it gets there.</para>
    /// </summary>
    /// <param name="includeSoftDeleted">Keep environments the customer has deleted but can still restore.</param>
    public async Task<List<UpgradeFleetRow>> ListFleetAsync(
        bool includeSoftDeleted = false, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var actionable = ProjectAccess.UpdateOpsProjectPredicate(snapshot);
        var manageable = ProjectAccess.ManageProjectPredicate(snapshot);

        var query = _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.MissingSince == null);

        if (!includeSoftDeleted)
        {
            query = query.Where(EnvironmentQueries.NotSoftDeleted);
        }

        var rows = await query
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .Select(ToRow(actionable, manageable))
            .ToListAsync(ct).ConfigureAwait(false);

        // Ordered in memory: "Production first" is a presentation rule, not something
        // worth a CASE expression in SQL, and the fleet is a page-sized list by design.
        return rows
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.IsProduction)
            .ThenBy(r => r.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One environment, for its own page - reached through the same visibility join as
    /// the fleet, so an id from a solution the caller cannot see answers exactly as an id
    /// that does not exist. Null for both, and for an environment Business Central no
    /// longer reports.
    ///
    /// <para>Soft-deleted environments are kept: "deleted, still restorable" is a state
    /// worth a page, and the links from the Environments list have to land somewhere.</para>
    /// </summary>
    public async Task<EnvironmentDetailRow?> GetEnvironmentAsync(int environmentId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var actionable = ProjectAccess.UpdateOpsProjectPredicate(snapshot);
        var manageable = ProjectAccess.ManageProjectPredicate(snapshot);

        var found = _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.MissingSince == null)
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null));

        var row = await found.Select(ToRow(actionable, manageable)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;

        // The same filtered query, so visibility is decided once and not re-argued here.
        var rest = await found
            .Select(e => new
            {
                e.Project!.CreatedByUserId,
                e.CountryCode,
                e.LocationName,
                e.UpdateWindowStart,
                e.UpdateWindowEnd,
                e.BcUpdateWindowStart,
                e.BcUpdateWindowEnd,
                e.BcUpdateWindowTimeZoneIana,
                e.BcUpdateWindowFetchedAt,
                e.AppSourceAppsUpdateCadence,
            })
            .FirstAsync(ct).ConfigureAwait(false);

        return new EnvironmentDetailRow(
            row,
            rest.CreatedByUserId,
            rest.CountryCode,
            rest.LocationName,
            rest.UpdateWindowStart,
            rest.UpdateWindowEnd,
            rest.BcUpdateWindowStart,
            rest.BcUpdateWindowEnd,
            rest.BcUpdateWindowTimeZoneIana,
            rest.BcUpdateWindowFetchedAt,
            rest.AppSourceAppsUpdateCadence);
    }

    /// <summary>
    /// The fleet with each row's detail - what <see cref="GetEnvironmentAsync"/> gives one
    /// environment - for a caller that needs the windows of every row, which is the
    /// <c>list_environments</c> MCP tool. One extra query for the whole list rather than
    /// one per row, and it goes through the same visibility join as the fleet itself, so
    /// the two halves cannot disagree about which environments exist.
    /// </summary>
    public async Task<List<EnvironmentDetailRow>> ListFleetDetailsAsync(
        bool includeSoftDeleted = false, CancellationToken ct = default)
    {
        var fleet = await ListFleetAsync(includeSoftDeleted, ct).ConfigureAwait(false);
        if (fleet.Count == 0) return [];

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var ids = fleet.Select(r => r.EnvironmentId).ToList();
        var rest = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .Select(e => new
            {
                e.Id,
                e.Project!.CreatedByUserId,
                e.CountryCode,
                e.LocationName,
                e.UpdateWindowStart,
                e.UpdateWindowEnd,
                e.BcUpdateWindowStart,
                e.BcUpdateWindowEnd,
                e.BcUpdateWindowTimeZoneIana,
                e.BcUpdateWindowFetchedAt,
                e.AppSourceAppsUpdateCadence,
            })
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        return fleet
            .Where(r => rest.ContainsKey(r.EnvironmentId))
            .Select(r =>
            {
                var x = rest[r.EnvironmentId];
                return new EnvironmentDetailRow(
                    r, x.CreatedByUserId, x.CountryCode, x.LocationName,
                    x.UpdateWindowStart, x.UpdateWindowEnd,
                    x.BcUpdateWindowStart, x.BcUpdateWindowEnd,
                    x.BcUpdateWindowTimeZoneIana, x.BcUpdateWindowFetchedAt,
                    x.AppSourceAppsUpdateCadence);
            })
            .ToList();
    }

    /// <summary>
    /// What Business Central last reported as installed in one environment, from the
    /// <c>oe_environment_apps</c> mirror - never a live read, so it answers for anyone who
    /// can see the solution without the customer's credentials. Null when the environment
    /// is not one this caller can see (the same answer as one that does not exist); empty
    /// when it has not been read yet. Each app says whether this workbench has ever
    /// delivered it to the environment, by app id, which is what tells "one of ours" from
    /// a vendor's.
    /// </summary>
    public async Task<List<InstalledAppRow>?> ListInstalledAppsAsync(int environmentId, CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.MissingSince == null)
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .Select(e => new { e.ProjectId, e.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (env is null) return null;

        var apps = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => a.EnvironmentId == environmentId)
            .Select(a => new { a.AppId, a.Name, a.Publisher, a.Version, a.FetchedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var delivered = (await _db.OeProjectDeliveryResults.AsNoTracking()
                .Where(r => r.AppId != null
                            && r.ProjectDelivery!.ProjectId == env.ProjectId
                            && r.ProjectDelivery.EnvironmentName == env.Name)
                .Select(r => r.AppId!)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .ToHashSet();

        return apps
            .OrderBy(a => a.Publisher, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new InstalledAppRow(a.AppId, a.Name, a.Publisher, a.Version, a.FetchedAt, delivered.Contains(a.AppId)))
            .ToList();
    }

    /// <summary>
    /// The fleet row for one environment. Shared by the list and the single read so the
    /// two cannot disagree about what a row says; both "may act" answers stay subqueries
    /// either way.
    /// </summary>
    private Expression<Func<OeProjectEnvironment, UpgradeFleetRow>> ToRow(
        Expression<Func<OeProject, bool>> actionable,
        Expression<Func<OeProject, bool>> manageable) =>
        e => new UpgradeFleetRow(
            e.ProjectId,
            e.Project!.Name,
            e.Project!.BcTimeZone,
            e.Id,
            e.Name,
            e.Type,
            e.Status,
            e.Version,
            e.BcNextUpdateVersion,
            e.BcNextUpdateType,
            e.BcNextUpdateStatus,
            e.BcNextUpdateDate,
            e.BcNextUpdateLatestDate,
            e.BcNextUpdateIgnoresWindow,
            e.BcNextUpdateFetchedAt,
            _db.OeProjects.Where(actionable).Any(p => p.Id == e.ProjectId),
            e.FetchedAt,
            e.AadTenantId ?? e.Project!.BcTenantId,
            e.BcDatabaseKb,
            e.Project!.BcStorageQuotaKb,
            // The allowance is the tenant's, so what counts against it is every
            // environment the customer has, not just the ones on this page.
            _db.OeProjectEnvironments
                .Where(x => x.ProjectId == e.ProjectId && x.MissingSince == null)
                .Sum(x => x.BcDatabaseKb),
            e.SoftDeletedOn,
            e.HardDeletePendingOn,
            _db.OeProjects.Where(manageable).Any(p => p.Id == e.ProjectId));

    /// <summary>
    /// Asks Business Central for fresh answers about <paramref name="projectIds"/> by
    /// handing each project to the existing <see cref="EnvironmentRefreshQueue"/> — the
    /// same worker the nightly sweep feeds, so a hand-triggered refresh and a sweep
    /// coalesce instead of making the round trips twice.
    ///
    /// <para>A project the caller may not act on is <em>skipped</em>, not refused: the
    /// page offers this over a selection, and one row the person cannot touch must not
    /// cost them the other ninety-nine. Returns how many projects were newly queued and
    /// how many were already in flight, so the page can say what happened.</para>
    /// </summary>
    public async Task<UpgradeRefreshResult> RequestRefreshAsync(
        IEnumerable<int> projectIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException(
                "No organization in scope; an environment refresh was requested outside an authenticated request.");

        var wanted = projectIds.Distinct().ToList();
        if (wanted.Count == 0) return new UpgradeRefreshResult(0, 0, 0);

        // One query for the whole selection, and the ops axis is the gate: the refresh
        // reads the customer's tenant with their credentials, so it is the same grant
        // the two write actions need rather than mere visibility.
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var actionable = ProjectAccess.UpdateOpsProjectPredicate(snapshot);
        var allowed = await _db.OeProjects.AsNoTracking()
            .Where(actionable)
            .Where(p => wanted.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var identity = new AmbientOrganizationScope.OrganizationIdentity(
            orgId, _orgContext.CurrentUserId, _orgContext.IsSiteAdmin, _orgContext.IsSystemOrganization);

        var queued = 0;
        var alreadyRunning = 0;
        foreach (var projectId in allowed)
        {
            if (await _refreshQueue.EnqueueAsync(new EnvironmentRefreshJob(projectId, identity), ct).ConfigureAwait(false))
            {
                queued++;
            }
            else
            {
                alreadyRunning++;
            }
        }

        _logger.LogInformation(
            "User {UserId} asked for an environment refresh of {Queued} project(s) ({AlreadyRunning} already running, {Skipped} not permitted).",
            _orgContext.CurrentUserId, queued, alreadyRunning, wanted.Count - allowed.Count);

        return new UpgradeRefreshResult(queued, alreadyRunning, wanted.Count - allowed.Count);
    }
}

/// <summary>
/// One environment on the Upgrades page: which customer it belongs to, what Business
/// Central says about it, the mirrored next platform update, and whether this caller may
/// move that update's date.
/// </summary>
/// <param name="TimeZone">
/// The customer's own IANA time zone, from their Business Central connection. A slot
/// somebody books is picked and shown in it — "tonight at 20:00" is always the
/// customer's evening, never ours. Null when nobody has set one; the page then says UTC
/// rather than guessing.
/// </param>
/// <param name="CanAct">
/// True when the caller holds the environment-updates grant on this row's project —
/// what decides whether the row gets a checkbox or a lock. Never a substitute for the
/// service-side check: <c>ProjectConnectionService</c> re-checks on every write.
/// </param>
public sealed record UpgradeFleetRow(
    int ProjectId,
    string ProjectName,
    string? TimeZone,
    int EnvironmentId,
    string EnvironmentName,
    string EnvironmentType,
    string? Status,
    string? Version,
    string? NextUpdateVersion,
    string? NextUpdateType,
    string? NextUpdateStatus,
    DateTime? NextUpdateDate,
    DateTime? NextUpdateLatestDate,
    bool? NextUpdateIgnoresWindow,
    DateTime? FetchedAt,
    bool CanAct,
    /// <summary>
    /// When the environment row itself was last read — its status, type and version.
    /// Deliberately separate from <see cref="FetchedAt"/>, which is the age of the
    /// <em>next-update</em> mirror: the two are stamped by different reads and a
    /// tenant can answer one and refuse the other. A page about update dates wants
    /// the first; a page about environments wants this one, or it would report an
    /// environment as never checked when only its updates were unreadable.
    /// </summary>
    DateTime? EnvironmentFetchedAt = null,
    /// <summary>
    /// The customer's Entra tenant: the one Business Central reported for the environment,
    /// else the one the solution's connection was set up with. Only here to build
    /// <see cref="BusinessCentralUrl"/>.
    /// </summary>
    Guid? TenantId = null,
    /// <summary>This environment's database size in kilobytes; null when not read.</summary>
    long? DatabaseKb = null,
    /// <summary>What the customer's tenant is allowed across all its environments, in kilobytes.</summary>
    long? TenantQuotaKb = null,
    /// <summary>What all the tenant's environments use together, in kilobytes. Filled by the list, not the query.</summary>
    long? TenantUsedKb = null,
    /// <summary>When the customer deleted the environment; null for one that is still live.</summary>
    DateTime? SoftDeletedOn = null,
    /// <summary>
    /// When Business Central stops keeping the deleted environment and it is gone for
    /// good. Null when Microsoft did not say, which is a different fact from "not deleted"
    /// and the pages word it as one.
    /// </summary>
    DateTime? HardDeletePendingOn = null,
    /// <summary>
    /// True when the caller manages this row's solution - its owner, an org admin, or
    /// anyone on a team assigned to it. What decides whether a list offers this row an
    /// action that only a manager may take; never a substitute for the service-side
    /// check, which <see cref="ProjectConnectionService"/> makes on every write.
    /// </summary>
    bool CanManage = false)
{
    /// <summary>
    /// True for an environment the customer deleted and Business Central is still
    /// keeping. Either signal counts, for the reason the mirror stores both. Nothing can
    /// be published, updated or rescheduled on one, so it is kept out of the working
    /// lists and shown under its own view.
    /// </summary>
    public bool IsSoftDeleted => SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(Status);

    /// <summary>
    /// How full the customer's tenant is, as a fraction; above 1 when over its allowance,
    /// which Business Central permits. Null until both halves have been read.
    /// </summary>
    public double? TenantStorageUse => TenantQuotaKb is > 0 && TenantUsedKb is { } used
        ? (double)used / TenantQuotaKb.Value
        : null;

    /// <summary>
    /// The environment in Business Central's own web client, or null when the tenant is
    /// not known. The environment name is a path segment, so it is escaped.
    /// </summary>
    public string? BusinessCentralUrl => TenantId is { } tenant
        ? $"https://businesscentral.dynamics.com/{tenant:D}/{Uri.EscapeDataString(EnvironmentName)}"
        : null;

    /// <summary>
    /// The customer tenant's Business Central admin centre, or null when the tenant is not
    /// known. Microsoft has no deep link to one environment's page in it. Read by the
    /// environment page's header and by the command palette's context block, which must
    /// offer the link exactly when the page does.
    /// </summary>
    public string? AdminCentreUrl => TenantId is { } tenant
        ? $"https://businesscentral.dynamics.com/{tenant:D}/admin"
        : null;

    /// <summary>True for a Production environment — the one the sweep is really about.</summary>
    public bool IsProduction =>
        string.Equals(EnvironmentType, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when there is an update to act on at all. Drives the "update available"
    /// filter and the per-row skip messages the confirm dialogs preview.
    /// </summary>
    public bool HasUpdate => !string.IsNullOrWhiteSpace(NextUpdateVersion);

    /// <summary>
    /// The last date the update can actually be moved to, which is a day earlier than
    /// <see cref="NextUpdateLatestDate"/> whenever Business Central's bound is a midnight
    /// day boundary — see <see cref="BcUpdateSchedule"/>. This is the date to show and the
    /// date the write aims at; the raw bound is kept on the row only because it is what
    /// the mirror stores.
    /// </summary>
    public DateTime? EffectiveLatestDate => BcUpdateSchedule.EffectiveLatestUtc(NextUpdateLatestDate);

    /// <summary>
    /// True when the update's date can still be moved further out — there is an update,
    /// Business Central gave it a last possible date, and the date is still short of that
    /// day. The page shows the same answer the service enforces, so a preview and the run
    /// agree, and both compare calendar days in UTC because the stored date is the start
    /// of the customer's update window rather than the bound itself — a window opening
    /// after midnight UTC puts it on the day after, which is still nowhere left to move.
    /// </summary>
    public bool CanPushDate =>
        HasUpdate && EffectiveLatestDate is { } latest
        && (NextUpdateDate is not { } scheduled || scheduled.Date < latest.Date);
}

/// <summary>
/// What a refresh request did: how many projects were newly queued, how many were
/// already being refreshed (a sweep or another person got there first), and how many
/// were left alone because the caller may not act on them.
/// </summary>
public sealed record UpgradeRefreshResult(int Queued, int AlreadyRunning, int Skipped);

/// <summary>
/// One environment for its own page: the fleet row, plus what only that page shows -
/// where the environment is, the two windows, and the AppSource cadence.
/// </summary>
/// <param name="CreatedByUserId">The solution's owner, which the manage check needs.</param>
public sealed record EnvironmentDetailRow(
    UpgradeFleetRow Fleet,
    int? CreatedByUserId,
    string? CountryCode,
    string? LocationName,
    TimeOnly? DeliveryWindowStart,
    TimeOnly? DeliveryWindowEnd,
    TimeOnly? BcUpdateWindowStart,
    TimeOnly? BcUpdateWindowEnd,
    string? BcUpdateWindowTimeZoneIana,
    DateTime? BcUpdateWindowFetchedAt,
    string? AppSourceAppsUpdateCadence);

/// <summary>
/// One app from the installed-apps mirror. <paramref name="DeliveredFromWorkbench"/> is true
/// when a delivery from this workbench has carried the app to the environment.
/// </summary>
public sealed record InstalledAppRow(
    Guid AppId, string Name, string Publisher, string Version, DateTime FetchedAt, bool DeliveredFromWorkbench);
