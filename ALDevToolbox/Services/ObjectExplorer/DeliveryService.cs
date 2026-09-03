using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Creates and runs <see cref="ProjectDelivery"/> runs — the publish side of SaaS
/// delivery. A release of a successful build to a release pipeline's target is created
/// here (access-gated, target snapshotted) and enqueued to <see cref="DeliveryQueue"/>;
/// <see cref="DeliveryWorker"/> then calls <see cref="RunDeliveryAsync"/>, which claims
/// the row and drives the per-app upload → install → poll flow through the
/// <see cref="IBcAppManagementClient"/> seam. Every failure is captured onto the row
/// (never thrown out of the worker). The BC secret never passes through here — only a
/// bearer token from <see cref="ProjectConnectionService"/>. See
/// <c>.design/saas-delivery.md</c> ("Publish flow", "Services &amp; seams").
/// </summary>
public sealed class DeliveryService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;
    private readonly DeliveryQueue _queue;
    private readonly ILogger<DeliveryService> _logger;

    public DeliveryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        IDeliveryTokenSource tokens,
        IBcAppManagementClient apps,
        IBcAdminClient admin,
        DeliveryQueue queue,
        ILogger<DeliveryService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _tokens = tokens;
        _apps = apps;
        _admin = admin;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>How long to wait between install-operation polls. Shortened by tests.</summary>
    internal TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for one app's install before giving up. Shortened by tests.</summary>
    internal TimeSpan PollTimeoutPerApp { get; set; } = TimeSpan.FromMinutes(10);

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; delivery called outside an authenticated request.");

    // ── Create + enqueue ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a delivery of <paramref name="projectBuildId"/> through
    /// <paramref name="releasePipelineId"/> to run immediately (the "Release now" path).
    /// Thin wrapper over <see cref="ScheduleDeliveryAsync"/> with <c>scheduledFor = now</c>.
    /// </summary>
    public Task<int> ReleaseBuildNowAsync(int releasePipelineId, int projectBuildId, CancellationToken ct = default)
        => ScheduleDeliveryAsync(releasePipelineId, projectBuildId, DateTime.UtcNow, ct);

    /// <summary>
    /// Creates a delivery of <paramref name="projectBuildId"/> through
    /// <paramref name="releasePipelineId"/>, scheduled for <paramref name="scheduledForUtc"/>.
    /// Validates access (the project owner / org Admin), that the build is a successful
    /// build of the release pipeline's build pipeline with deliverables, that the target
    /// environment can take an install, and that the pipeline's install timing is one
    /// Business Central still accepts. Snapshots the target so later edits don't
    /// rewrite history, and records whether the chosen time is <em>outside</em> the
    /// environment's update window (the audited override). A delivery due now/in the past
    /// is enqueued immediately; a future one is left for <see cref="DeliveryScheduler"/> to
    /// enqueue when due. Returns the new delivery id. Throws
    /// <see cref="PlanValidationException"/> on a bad request,
    /// <see cref="ProjectAccessDeniedException"/> when not permitted.
    /// </summary>
    public async Task<int> ScheduleDeliveryAsync(int releasePipelineId, int projectBuildId, DateTime scheduledForUtc, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        scheduledForUtc = DateTime.SpecifyKind(scheduledForUtc, DateTimeKind.Utc);

        var rp = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new
            {
                r.Id,
                r.ProjectId,
                r.BuildPipelineId,
                r.DeploymentSchedule,
                r.SchemaSyncMode,
                OwnerId = r.Project!.CreatedByUserId,
                TimeZone = r.Project.BcTimeZone,
                EnvName = r.ProjectEnvironment!.Name,
                EnvMissing = r.ProjectEnvironment.MissingSince != null,
                EnvStatus = r.ProjectEnvironment.Status,
                WindowStart = r.ProjectEnvironment.UpdateWindowStart,
                WindowEnd = r.ProjectEnvironment.UpdateWindowEnd,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("ReleasePipeline", "This release pipeline no longer exists.");

        await _access.EnsureCanManageAsync(rp.ProjectId, rp.OwnerId, ct);

        if (rp.EnvMissing)
        {
            throw Validation("ProjectEnvironment",
                "This release pipeline's target environment is no longer present in Business Central. Refresh the environments and try again.");
        }
        // The cached status catches the obvious cases at the point the user is looking
        // at the screen. The live re-read before the upload (see PublishAsync) is the
        // one that catches an update that lands between scheduling and running.
        if (BcEnvironmentStatus.RefusalMessage(rp.EnvName, rp.EnvStatus) is { } statusRefusal)
        {
            throw Validation("ProjectEnvironment", statusRefusal);
        }

        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == projectBuildId)
            .Select(b => new { b.Id, b.ProjectId, b.PipelineId, b.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Build", "That build no longer exists.");

        if (build.ProjectId != rp.ProjectId || build.PipelineId != rp.BuildPipelineId)
        {
            throw Validation("Build", "That build isn't from this release pipeline's build pipeline.");
        }
        if (build.Status != ProjectBuildStatus.Ready)
        {
            throw Validation("Build", "Only a successful build can be released.");
        }

        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == build.Id)
            .OrderBy(a => a.Id)
            .Select(a => new { a.AppName, a.AppVersion })
            .ToListAsync(ct);
        if (artifacts.Count == 0)
        {
            throw Validation("Build", "That build has no deliverable apps to publish.");
        }

        // A release pipeline saved before the move to the App Management API stores the
        // old wording ("Current Version", "Force Sync"), which that API rejects outright.
        // Refuse here with something the user can act on rather than letting the upload
        // fail hours later inside the worker.
        if (!BcDeploymentSchedule.IsValid(rp.DeploymentSchedule))
        {
            throw Validation("DeploymentSchedule",
                "This release pipeline's install timing is no longer a valid option. Open the release pipeline, choose when installs should run, and save it.");
        }
        if (!BcSyncMode.IsValid(rp.SchemaSyncMode))
        {
            throw Validation("SchemaSyncMode",
                "This release pipeline's schema sync setting is no longer a valid option. Open the release pipeline, choose a schema sync setting, and save it.");
        }
        // Business Central decides the order it installs a window's queue in; our order
        // only decides the order things were uploaded. With one app that's harmless, with
        // several it can install a dependent before its dependency.
        if (artifacts.Count > 1 && BcDeploymentSchedule.IsDeferred(rp.DeploymentSchedule))
        {
            throw Validation("DeploymentSchedule",
                $"This build has {artifacts.Count} apps, and Business Central chooses the order it installs them in when they wait for a later update. "
                + "Set this release pipeline to install right away, or release the apps one at a time.");
        }

        // Audit the override: a window exists and the chosen time falls outside it.
        var tz = UpdateWindow.ResolveTimeZone(rp.TimeZone);
        var outsideWindow = UpdateWindow.IsConfigured(rp.WindowStart, rp.WindowEnd)
            && !UpdateWindow.IsWithin(rp.WindowStart, rp.WindowEnd, tz, scheduledForUtc);

        var now = DateTime.UtcNow;
        var delivery = new ProjectDelivery
        {
            OrganizationId = orgId,
            ProjectId = rp.ProjectId,
            ReleasePipelineId = rp.Id,
            ProjectBuildId = build.Id,
            TriggeredByUserId = _orgContext.CurrentUserId,
            EnvironmentName = rp.EnvName,
            DeploymentSchedule = rp.DeploymentSchedule,
            SchemaSyncMode = rp.SchemaSyncMode,
            ScheduledFor = scheduledForUtc,
            ScheduledOutsideWindow = outsideWindow,
            Status = ProjectDeliveryStatus.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        for (var i = 0; i < artifacts.Count; i++)
        {
            delivery.Results.Add(new ProjectDeliveryResult
            {
                OrganizationId = orgId,
                Ordering = i,
                AppName = artifacts[i].AppName,
                AppVersion = artifacts[i].AppVersion,
                Status = ProjectDeliveryResultStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _db.OeProjectDeliveries.Add(delivery);
        await _db.SaveChangesAsync(ct);

        // Due now (or in the past) → enqueue immediately so "Release now" is snappy;
        // a future delivery is left for the DeliveryScheduler to enqueue when due.
        if (scheduledForUtc <= now)
        {
            await _queue.EnqueueAsync(new DeliveryJob(delivery.Id, AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "capturing identity for a delivery")), ct);
        }

        _logger.LogInformation(
            "Created delivery {DeliveryId}: build {BuildId} → release pipeline {ReleasePipelineId} ({Env}) for {ScheduledFor:o}, {AppCount} app(s){Override}.",
            delivery.Id, build.Id, rp.Id, rp.EnvName, scheduledForUtc, artifacts.Count,
            outsideWindow ? " (outside the update window)" : "");
        return delivery.Id;
    }

    /// <summary>
    /// Cancels a <em>scheduled</em> delivery (atomic <c>scheduled → cancelled</c>). Access-gated.
    /// Throws <see cref="PlanValidationException"/> if it's already been claimed (the
    /// "cancellable until a worker picks it up" guarantee) or no longer exists.
    /// </summary>
    public async Task CancelDeliveryAsync(int deliveryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var owner = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ProjectId, OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That delivery no longer exists.");
        await _access.EnsureCanManageAsync(owner.ProjectId, owner.OwnerId, ct);

        var now = DateTime.UtcNow;
        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Cancelled)
                .SetProperty(d => d.FinishedAt, now)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be cancelled.");
        }
        _logger.LogInformation("Cancelled delivery {DeliveryId}.", deliveryId);
    }

    /// <summary>
    /// Moves a <em>scheduled</em> delivery to a new time (atomic on <c>scheduled</c>),
    /// recomputing the outside-window audit flag. Access-gated. Throws if the delivery has
    /// already started or no longer exists. Enqueues immediately if the new time is now/past.
    /// </summary>
    public async Task RescheduleDeliveryAsync(int deliveryId, DateTime newScheduledForUtc, CancellationToken ct = default)
    {
        RequireOrganizationId();
        newScheduledForUtc = DateTime.SpecifyKind(newScheduledForUtc, DateTimeKind.Utc);

        var info = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new
            {
                d.OrganizationId,
                d.TriggeredByUserId,
                d.ProjectId,
                OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId,
                TimeZone = d.ReleasePipeline.Project.BcTimeZone,
                WindowStart = d.ReleasePipeline.ProjectEnvironment!.UpdateWindowStart,
                WindowEnd = d.ReleasePipeline.ProjectEnvironment.UpdateWindowEnd,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That delivery no longer exists.");
        await _access.EnsureCanManageAsync(info.ProjectId, info.OwnerId, ct);

        var tz = UpdateWindow.ResolveTimeZone(info.TimeZone);
        var outsideWindow = UpdateWindow.IsConfigured(info.WindowStart, info.WindowEnd)
            && !UpdateWindow.IsWithin(info.WindowStart, info.WindowEnd, tz, newScheduledForUtc);

        var now = DateTime.UtcNow;
        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScheduledFor, newScheduledForUtc)
                .SetProperty(d => d.ScheduledOutsideWindow, outsideWindow)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be rescheduled.");
        }

        if (newScheduledForUtc <= now)
        {
            await _queue.EnqueueAsync(new DeliveryJob(deliveryId,
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
                    info.OrganizationId, _orgContext.IsSystemOrganization, info.TriggeredByUserId)), ct);
        }
        _logger.LogInformation("Rescheduled delivery {DeliveryId} to {ScheduledFor:o}.", deliveryId, newScheduledForUtc);
    }

    // ── Scheduler sweep helpers (called per-org under an AmbientOrganizationScope) ──

    /// <summary>
    /// Enqueues every <c>scheduled</c> delivery in the current org whose time has come
    /// (<see cref="ProjectDelivery.ScheduledFor"/> ≤ <paramref name="nowUtc"/>). Org-scoped
    /// via the query filter (the scheduler sets the ambient org). Re-enqueuing a row the
    /// worker hasn't claimed yet is a no-op (the queue dedupes by id). Returns the count.
    /// </summary>
    public async Task<int> EnqueueDueDeliveriesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var due = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Status == ProjectDeliveryStatus.Scheduled && d.ScheduledFor <= nowUtc)
            .Select(d => new { d.Id, d.OrganizationId, d.TriggeredByUserId })
            .ToListAsync(ct);

        foreach (var d in due)
        {
            await _queue.EnqueueAsync(new DeliveryJob(d.Id,
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
                    d.OrganizationId, _orgContext.IsSystemOrganization, d.TriggeredByUserId)), ct);
        }
        return due.Count;
    }

    /// <summary>
    /// Fails every delivery in the current org left in a non-terminal <em>in-progress</em>
    /// state (<c>claimed</c>/<c>uploading</c>/<c>installing</c>) — orphaned when the process
    /// died mid-publish. Called once per org on the scheduler's first sweep after startup,
    /// when nothing is running yet, so it never trips an actively-running delivery. The
    /// publish isn't safely resumable (partial uploads to BC), so these are failed, not
    /// retried. Returns the count.
    /// </summary>
    public async Task<int> FailInterruptedDeliveriesAsync(CancellationToken ct = default)
    {
        var orphans = await _db.OeProjectDeliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Claimed
                        || d.Status == ProjectDeliveryStatus.Uploading
                        || d.Status == ProjectDeliveryStatus.Installing)
            .Include(d => d.Results)
            .ToListAsync(ct);
        if (orphans.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var d in orphans)
        {
            d.Status = ProjectDeliveryStatus.Failed;
            d.FailureMessage = "The delivery was interrupted by a restart. Release the build again.";
            d.FinishedAt = now;
            d.UpdatedAt = now;
            foreach (var r in d.Results.Where(r => r.Status is ProjectDeliveryResultStatus.Pending
                                                    or ProjectDeliveryResultStatus.Uploading
                                                    or ProjectDeliveryResultStatus.Installing))
            {
                r.Status = ProjectDeliveryResultStatus.Skipped;
                r.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Failed {Count} delivery(ies) interrupted by a restart.", orphans.Count);
        return orphans.Count;
    }

    // ── Run (worker entry) ────────────────────────────────────────────────────

    /// <summary>
    /// Claims the delivery (atomic <c>scheduled → claimed</c>) and runs the publish.
    /// Returns quietly if the row was already taken or cancelled. All failures are
    /// recorded on the row; this method does not throw on a publish failure.
    /// </summary>
    public async Task RunDeliveryAsync(int deliveryId, CancellationToken ct = default)
    {
        var claimedAt = DateTime.UtcNow;
        var claimed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed)
                .SetProperty(d => d.ClaimedAt, claimedAt)
                .SetProperty(d => d.UpdatedAt, claimedAt), ct);
        if (claimed == 0)
        {
            _logger.LogInformation("Delivery {DeliveryId} was already claimed or cancelled; skipping.", deliveryId);
            return;
        }

        var delivery = await _db.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null)
        {
            _logger.LogWarning("Delivery {DeliveryId} vanished after being claimed.", deliveryId);
            return;
        }

        var log = new StringBuilder();
        try
        {
            await PublishAsync(delivery, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailAsync(delivery, log, "The delivery was interrupted while the app was shutting down.", ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delivery {DeliveryId} failed unexpectedly.", deliveryId);
            await FailAsync(delivery, log, "The delivery failed unexpectedly. " + Short(ex.Message), ct);
        }
    }

    /// <summary>
    /// Reads the environment's installed apps once, then uploads each app in stored
    /// (dependency) order. An immediate install is polled to a terminal state; a deferred
    /// one is handed to Business Central and the delivery ends at
    /// <see cref="ProjectDeliveryStatus.HandedOff"/>.
    /// </summary>
    private async Task PublishAsync(ProjectDelivery delivery, StringBuilder log, CancellationToken ct)
    {
        BcDeliveryContext bc;
        try
        {
            bc = await _tokens.AcquireDeliveryContextAsync(delivery.ProjectId, ct);
        }
        catch (BcApiException ex)
        {
            await FailAsync(delivery, log, ex.Message, ct);
            return;
        }

        // Whatever the API called this environment's family, verbatim. Every App
        // Management URL is built from it.
        var family = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == delivery.ProjectId && e.Name == delivery.EnvironmentName)
            .Select(e => e.ApplicationFamily)
            .FirstOrDefaultAsync(ct)
            ?? BcConstants.DefaultApplicationFamily;

        // The environment was fine when this delivery was scheduled; hours may have
        // passed. Re-read it live before the first byte goes up — a platform update that
        // landed in between is exactly what this catches.
        if (await EnvironmentBlockedAsync(delivery, bc.AccessToken, ct) is { } blocked)
        {
            Append(log, blocked);
            await FailAsync(delivery, log, blocked, ct);
            return;
        }

        var now = DateTime.UtcNow;
        delivery.Status = ProjectDeliveryStatus.Uploading;
        delivery.StartedAt = now;
        delivery.UpdatedAt = now;
        Append(log, $"Publishing {delivery.Results.Count} app(s) to {delivery.EnvironmentName}.");
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);

        // Ordered artifacts line up 1:1 with the ordered result rows (both came from the
        // build's artifacts ordered by id at creation — that id order *is* the build's
        // dependency order, so nothing is re-sorted here). Load each blob only when it's
        // that app's turn, so we never hold every .app in memory at once.
        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == delivery.ProjectBuildId)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.FileName })
            .ToListAsync(ct);

        // One read of what's already installed. The API only accepts a deferred schedule
        // for an app it already knows, so a first-time upload has to be caught before
        // it's sent — a 400 from BC wouldn't say which rule it broke.
        IReadOnlyList<BcInstalledApp> installed;
        try
        {
            installed = await _apps.ListInstalledAppsAsync(bc.AccessToken, family, delivery.EnvironmentName, ct);
        }
        catch (BcApiException ex)
        {
            await FailAsync(delivery, log, $"Couldn't read the apps installed on {delivery.EnvironmentName}. " + Short(ex.Message), ct);
            return;
        }

        // "Next minor/major update" is an instruction to bump an app that's already
        // there. Business Central refuses it for an app it has never seen, so a
        // first-time release has to go in right away.
        if (BcDeploymentSchedule.RequiresInstalledApp(delivery.DeploymentSchedule))
        {
            var missing = delivery.Results
                .Where(r => !installed.Any(a => string.Equals(a.Name, r.AppName, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.AppName)
                .ToList();
            if (missing.Count > 0)
            {
                await FailAsync(delivery, log,
                    $"{string.Join(", ", missing)} isn't installed on {delivery.EnvironmentName} yet, and Business Central only accepts "
                    + "\"next minor update\" or \"next major update\" for an app that's already there. Install it right away first.", ct);
                return;
            }
        }

        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        var failedIndex = -1;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (failedIndex >= 0)
            {
                result.Status = ProjectDeliveryResultStatus.Skipped;
                result.UpdatedAt = DateTime.UtcNow;
                continue;
            }

            var label = $"{result.AppName} {result.AppVersion}";
            if (i >= artifacts.Count)
            {
                failedIndex = i;
                result.Status = ProjectDeliveryResultStatus.Failed;
                result.Message = "The build's deliverables changed under the delivery.";
                Append(log, $"FAILED {label}: deliverable missing.");
                break;
            }

            try
            {
                var artifact = artifacts[i];
                var bytes = await _db.OeProjectBuildArtifacts.AsNoTracking()
                    .Where(a => a.Id == artifact.Id)
                    .Select(a => a.Content)
                    .FirstAsync(ct);

                result.Status = ProjectDeliveryResultStatus.Uploading;
                result.UpdatedAt = DateTime.UtcNow;
                Append(log, $"Uploading {label} ({bytes.Length} bytes)...");
                await SaveResultAsync(delivery, log, ct);

                var operation = await _apps.InstallPteAsync(
                    bc.AccessToken, family, delivery.EnvironmentName,
                    bytes, artifact.FileName,
                    delivery.DeploymentSchedule, delivery.SchemaSyncMode,
                    // No language is sent. The toolbox has no concept of one, and
                    // guessing "en-US" would set the install locale wrong for (say) a
                    // Danish customer; BC applies its own default until a release
                    // pipeline can say what the language should be.
                    languageId: string.Empty,
                    installOrUpdateNeededDependencies: true,
                    ct);

                result.OperationId = operation.Id;
                result.AppId = operation.AppId?.ToString();
                result.UpdatedAt = DateTime.UtcNow;

                // BC read the version out of the package it just accepted. If that isn't
                // the version this delivery promised, something other than this build's
                // artifact is going in and the history would misreport it.
                if (!string.IsNullOrEmpty(operation.TargetAppVersion)
                    && !string.Equals(operation.TargetAppVersion, result.AppVersion, StringComparison.OrdinalIgnoreCase))
                {
                    failedIndex = i;
                    result.Status = ProjectDeliveryResultStatus.Failed;
                    result.Message = $"Business Central read version {operation.TargetAppVersion} from the uploaded app, but this build says {result.AppVersion}.";
                    Append(log, $"FAILED {label}: {result.Message}");
                    await SaveResultAsync(delivery, log, ct);
                    break;
                }

                if (BcDeploymentSchedule.IsDeferred(delivery.DeploymentSchedule))
                {
                    // Nothing left to watch: BC runs this in its own window, and no poll
                    // of ours would ever see it go terminal.
                    result.Status = ProjectDeliveryResultStatus.Scheduled;
                    Append(log, $"Business Central has scheduled {label} (operation {operation.Id}).");
                    await SaveResultAsync(delivery, log, ct);
                    continue;
                }

                result.Status = ProjectDeliveryResultStatus.Installing;
                result.UpdatedAt = DateTime.UtcNow;
                delivery.Status = ProjectDeliveryStatus.Installing;
                delivery.UpdatedAt = DateTime.UtcNow;
                Append(log, $"Installing {label} (operation {operation.Id})...");
                await SaveResultAsync(delivery, log, ct);

                var outcome = await PollUntilTerminalAsync(bc, family, delivery, operation, ct);
                if (outcome.Completed)
                {
                    result.Status = ProjectDeliveryResultStatus.Completed;
                    result.Message = outcome.Message;
                    Append(log, $"Installed {label}.");
                }
                else
                {
                    failedIndex = i;
                    result.Status = ProjectDeliveryResultStatus.Failed;
                    result.Message = outcome.Message;
                    Append(log, $"FAILED {label}: {outcome.Message}");
                }
                result.UpdatedAt = DateTime.UtcNow;
                await SaveResultAsync(delivery, log, ct);
            }
            catch (BcApiException ex)
            {
                failedIndex = i;
                result.Status = ProjectDeliveryResultStatus.Failed;
                result.Message = Short(ex.Message);
                result.UpdatedAt = DateTime.UtcNow;
                Append(log, $"FAILED {label}: {Short(ex.Message)}");
            }
        }

        var endNow = DateTime.UtcNow;
        if (failedIndex >= 0)
        {
            var failed = results[failedIndex];
            delivery.Status = ProjectDeliveryStatus.Failed;
            delivery.FailureMessage = $"{failed.AppName} {failed.AppVersion} failed: {failed.Message}";
            Append(log, "Delivery failed.");
        }
        else if (BcDeploymentSchedule.IsDeferred(delivery.DeploymentSchedule))
        {
            // Uploaded and accepted, but the install happens on Business Central's
            // schedule. We're no longer driving it, so this is as far as the delivery goes.
            delivery.Status = ProjectDeliveryStatus.HandedOff;
            Append(log, "Business Central has accepted the apps and will install them on its own schedule.");
        }
        else
        {
            delivery.Status = ProjectDeliveryStatus.Deployed;
            Append(log, "Delivery complete.");
        }
        delivery.FinishedAt = endNow;
        delivery.UpdatedAt = endNow;
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-reads the target environment from the Admin Center API and returns a refusal
    /// message when it can't take an install right now — the claim-time half of the
    /// status gate (the other half runs in <see cref="ScheduleDeliveryAsync"/>). Returns
    /// null when the delivery may proceed, including when the API can't be read: a
    /// transport failure here isn't evidence the environment is unhealthy, and the
    /// upload that follows will surface a real problem with a better message. The fresh
    /// status is written back to the environment row so the project page doesn't keep
    /// showing the stale one the delivery just contradicted.
    /// </summary>
    private async Task<string?> EnvironmentBlockedAsync(ProjectDelivery delivery, string token, CancellationToken ct)
    {
        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == delivery.ProjectId && e.Name == delivery.EnvironmentName)
            .Select(e => new { e.Id, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct);

        BcEnvironment? live;
        try
        {
            live = await _admin.GetEnvironmentAsync(token, env?.ApplicationFamily, delivery.EnvironmentName, ct);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("Delivery {DeliveryId}: couldn't re-read environment {Env} before publishing: {Message}.",
                delivery.Id, delivery.EnvironmentName, ex.Message);
            return null;
        }

        if (live is null)
        {
            return $"Business Central no longer has an environment called '{delivery.EnvironmentName}'. Refresh the environments on the project's Business Central page.";
        }

        if (env is not null)
        {
            var stamped = DateTime.UtcNow;
            await _db.OeProjectEnvironments.Where(e => e.Id == env.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, live.Status)
                    .SetProperty(e => e.StatusFetchedAt, stamped), ct);
        }

        var refusal = BcEnvironmentStatus.RefusalMessage(delivery.EnvironmentName, live.Status);
        if (refusal is not null)
        {
            _logger.LogWarning("Delivery {DeliveryId} refused: environment {Env} is {Status}.",
                delivery.Id, delivery.EnvironmentName, live.Status);
        }
        return refusal;
    }

    /// <summary>
    /// Polls one install operation by id until it reports a terminal state or the per-app
    /// timeout elapses. Keyed on the operation and app ids Business Central returned from
    /// the upload, so two extensions that happen to share a display name can't be mistaken
    /// for each other.
    /// </summary>
    private async Task<DeploymentOutcome> PollUntilTerminalAsync(
        BcDeliveryContext bc, string family, ProjectDelivery delivery, BcAppOperation started, CancellationToken ct)
    {
        if (started.AppId is not { } appId)
        {
            // Without an app id there's nothing to poll. The upload was accepted, so
            // don't call it a failure — say what's unverified and let the consultant look.
            return new DeploymentOutcome(true, "Business Central accepted the upload but didn't say which app it was, so the install wasn't confirmed here.");
        }

        var deadline = DateTime.UtcNow + PollTimeoutPerApp;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var operation = await _apps.GetAppOperationAsync(
                bc.AccessToken, family, delivery.EnvironmentName, appId, started.Id, ct);

            switch (operation?.Status)
            {
                case BcAppOperationStatus.Succeeded:
                    return new DeploymentOutcome(true, null);
                case BcAppOperationStatus.Failed:
                    return new DeploymentOutcome(false, DescribeFailure(operation));
                case BcAppOperationStatus.Canceled:
                    return new DeploymentOutcome(false, "The install was cancelled in Business Central.");
                case BcAppOperationStatus.Skipped:
                    return new DeploymentOutcome(false, "Business Central skipped the install.");
                // Scheduled / Running / Unknown, and a not-yet-visible operation → keep polling.
            }

            if (DateTime.UtcNow > deadline)
            {
                return new DeploymentOutcome(false, "Timed out waiting for the install to finish.");
            }
            await Task.Delay(PollDelay, ct);
        }
    }

    /// <summary>
    /// Turns a failed operation into one line for the history. The codes lead because
    /// <see cref="BcAppOperation.ErrorMessage"/> comes back in the <em>environment's</em>
    /// language — useful to show, never to branch on.
    /// </summary>
    private static string DescribeFailure(BcAppOperation operation)
    {
        var codes = new[] { operation.ErrorCode, operation.InnerErrorCode }
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();
        var detail = codes.Count > 0
            ? $"Business Central reported the install as failed ({string.Join(" / ", codes)})."
            : "Business Central reported the install as failed.";
        return string.IsNullOrWhiteSpace(operation.ErrorMessage)
            ? detail
            : Short($"{detail} {operation.ErrorMessage}");
    }

    private sealed record DeploymentOutcome(bool Completed, string? Message);

    // ── Reads (for delivery history) ──────────────────────────────────────────

    /// <summary>A release pipeline's deliveries, newest first, without the per-app rows or blobs.</summary>
    public async Task<List<ProjectDelivery>> ListDeliveriesAsync(int releasePipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(releasePipelineId, ct);
        return await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == releasePipelineId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// A release pipeline's deliveries, newest first, each with its per-app result rows
    /// (ordered) — for the delivery-history UI. The result rows carry no blobs, so this
    /// stays cheap. Triggering-user display names are resolved alongside.
    /// </summary>
    public async Task<List<DeliveryHistoryRow>> ListDeliveryHistoryAsync(int releasePipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(releasePipelineId, ct);
        return await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == releasePipelineId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeliveryHistoryRow(
                d.Id,
                d.ProjectBuildId,
                d.Status,
                d.EnvironmentName,
                d.ScheduledFor,
                d.ScheduledOutsideWindow,
                d.CreatedAt,
                d.StartedAt,
                d.FinishedAt,
                d.FailureMessage,
                d.TriggeredByUser != null ? d.TriggeredByUser.DisplayName : null,
                d.Results.OrderBy(r => r.Ordering)
                    .Select(r => new DeliveryAppRow(r.AppName, r.AppVersion, r.Status, r.Message))
                    .ToList()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gates a release-pipeline-keyed delivery read on its project's visibility —
    /// deliveries inherit it like everything else under a project. One that doesn't
    /// exist passes; the read below returns nothing on its own.
    /// </summary>
    private async Task EnsureCanViewReleasePipelineAsync(int releasePipelineId, CancellationToken ct)
    {
        var projectId = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId)
            .Select(r => (int?)r.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SaveResultAsync(ProjectDelivery delivery, StringBuilder log, CancellationToken ct)
    {
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(ProjectDelivery delivery, StringBuilder log, string message, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        delivery.Status = ProjectDeliveryStatus.Failed;
        delivery.FailureMessage = message;
        delivery.FinishedAt = now;
        delivery.UpdatedAt = now;
        Append(log, "Delivery failed: " + message);
        delivery.DiagnosticsLog = log.ToString();
        // Any app still pending/uploading didn't get there.
        foreach (var r in delivery.Results.Where(r => r.Status is ProjectDeliveryResultStatus.Pending
                                                       or ProjectDeliveryResultStatus.Uploading
                                                       or ProjectDeliveryResultStatus.Installing))
        {
            r.Status = ProjectDeliveryResultStatus.Skipped;
            r.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static void Append(StringBuilder log, string line) =>
        log.Append(DateTime.UtcNow.ToString("HH:mm:ss")).Append("  ").AppendLine(line);

    private static string Short(string message) => message.Length > 300 ? message[..300] : message;

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>A delivery for the history list, with its per-app rows resolved for display.</summary>
public sealed record DeliveryHistoryRow(
    int Id,
    int ProjectBuildId,
    string Status,
    string EnvironmentName,
    DateTime ScheduledFor,
    bool ScheduledOutsideWindow,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? TriggeredByName,
    IReadOnlyList<DeliveryAppRow> Apps)
{
    /// <summary>True while the delivery is still working (so the page keeps polling).</summary>
    public bool IsLive => !ProjectDeliveryStatus.IsTerminal(Status);

    /// <summary>True while a worker is actively publishing — the page polls only for these (a far-future scheduled row needn't poll).</summary>
    public bool IsActive => Status is ProjectDeliveryStatus.Claimed or ProjectDeliveryStatus.Uploading or ProjectDeliveryStatus.Installing;

    /// <summary>True for a delivery waiting for its scheduled time — the cancellable/reschedulable state.</summary>
    public bool IsScheduled => Status == ProjectDeliveryStatus.Scheduled;
}

/// <summary>One app's outcome within a delivery, for the history's per-app breakdown.</summary>
public sealed record DeliveryAppRow(string AppName, string AppVersion, string Status, string? Message);
