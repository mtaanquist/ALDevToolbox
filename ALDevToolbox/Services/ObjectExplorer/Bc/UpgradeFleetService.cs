using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The read side of the Upgrades page: one row per Business Central environment across
/// every project the caller can see, answered entirely from the <c>bc_next_update_*</c>
/// mirror so a hundred customers list without a hundred live round trips. See
/// <c>.design/saas-delivery.md</c> and issue #657.
///
/// <para>
/// <b>The join is the guard.</b> <see cref="ProjectEnvironment"/> has no visibility rule
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
    /// (<see cref="ProjectEnvironment.MissingSince"/> null), with the mirrored next
    /// update and whether this caller may act on it.
    ///
    /// <para>The "may act" answer is computed as part of the same query — a subquery
    /// over <see cref="ProjectAccess.UpdateOpsProjectPredicate"/> — rather than a check
    /// per row, so a fleet of a hundred environments costs one round trip.</para>
    ///
    /// <para>Ordered by customer, then Production before everything else, then name:
    /// the sweep runs customer by customer, and Production is the row the upgrade team
    /// is looking for when it gets there.</para>
    /// </summary>
    public async Task<List<UpgradeFleetRow>> ListFleetAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var actionable = ProjectAccess.UpdateOpsProjectPredicate(snapshot);

        var rows = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.MissingSince == null)
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .Select(e => new UpgradeFleetRow(
                e.ProjectId,
                e.Project!.Name,
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
                _db.OeProjects.Where(actionable).Any(p => p.Id == e.ProjectId)))
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
/// <param name="CanAct">
/// True when the caller holds the environment-updates grant on this row's project —
/// what decides whether the row gets a checkbox or a lock. Never a substitute for the
/// service-side check: <c>ProjectConnectionService</c> re-checks on every write.
/// </param>
public sealed record UpgradeFleetRow(
    int ProjectId,
    string ProjectName,
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
    bool CanAct)
{
    /// <summary>True for a Production environment — the one the sweep is really about.</summary>
    public bool IsProduction =>
        string.Equals(EnvironmentType, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when there is an update to act on at all. Drives the "update available"
    /// filter and the per-row skip messages the confirm dialogs preview.
    /// </summary>
    public bool HasUpdate => !string.IsNullOrWhiteSpace(NextUpdateVersion);

    /// <summary>
    /// True when the update's date can still be moved further out — there is an update,
    /// Business Central gave it a last possible date, and it isn't already there. The
    /// page shows the same answer the service enforces, so a preview and the run agree.
    /// </summary>
    public bool CanPushDate =>
        HasUpdate && NextUpdateLatestDate is { } latest && NextUpdateDate != latest;
}

/// <summary>
/// What a refresh request did: how many projects were newly queued, how many were
/// already being refreshed (a sweep or another person got there first), and how many
/// were left alone because the caller may not act on them.
/// </summary>
public sealed record UpgradeRefreshResult(int Queued, int AlreadyRunning, int Skipped);
