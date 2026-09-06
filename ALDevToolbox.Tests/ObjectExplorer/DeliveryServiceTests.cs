using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Create + run for <see cref="DeliveryService"/> against the shared <see cref="TestDb"/>,
/// driving the publish orchestration through fake <see cref="IBcAppManagementClient"/>
/// and <see cref="IDeliveryTokenSource"/> seams (no real BC). Covers the snapshot at
/// creation, the validation guards, the happy-path upload→install→poll in dependency
/// order, partial failure (fail + skip the rest), a clean token failure, the claim
/// no-op, and the rules that exist only because Business Central does its own
/// scheduling: a deferred install is handed off rather than watched, and it is refused
/// where BC wouldn't honour our ordering. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class DeliveryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppManagementClient _apps = new();
    private readonly FakeTokenSource _tokens = new();
    private readonly FakeAdminClient _admin = new();
    private readonly DeliveryQueue _queue = new();

    public DeliveryServiceTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReleaseBuildNowAsync_creates_delivery_with_snapshot_and_pending_results()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.EnvironmentName.Should().Be("Production");
        delivery.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        delivery.SchemaSyncMode.Should().Be(BcSyncMode.Add);
        delivery.Results.Should().HaveCount(2);
        delivery.Results.Select(r => r.AppName).Should().Equal("CRONUS Core", "CRONUS Sales");
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Pending);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_non_successful_build()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, buildStatus: ProjectBuildStatus.Failed);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_an_environment_that_is_upgrading()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "Upgrading"));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironment"];
        error.Should().Contain("Upgrading", "the refusal names the status the consultant will see in Business Central");
    }

    [Fact]
    public async Task RunDeliveryAsync_fails_the_run_when_the_environment_started_upgrading_after_it_was_scheduled()
    {
        int deliveryId;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            // Active at scheduling time - the cached-status gate lets this through.
            deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        }

        // ...and an update lands in Business Central before the worker picks it up.
        _admin.OnGet = name => new BcEnvironment(name, "Production") { Status = "Upgrading" };

        await using (var run = _db.NewContext())
        {
            await NewService(run).RunDeliveryAsync(deliveryId);
        }

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("Upgrading");
        _admin.Requested.Should().Contain("Production", "the run re-reads the environment before uploading");
        _apps.UploadedOrder.Should().BeEmpty("nothing may be uploaded to an environment that can't take it");

        // The page shouldn't keep showing the status the delivery just contradicted.
        var env = await read.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Name == "Production" && e.ProjectId == delivery.ProjectId);
        env.Status.Should().Be("Upgrading");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_build_from_a_different_build_pipeline()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        // A second build pipeline in the same project, with its own successful build.
        var otherPipeline = await SeedPipelineAsync(ctx, seed.ProjectId);
        var otherBuild = await SeedBuildAsync(ctx, seed.ProjectId, otherPipeline, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, otherBuild);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    /// <summary>
    /// A release pipeline that draws from a repository's GitHub releases takes the
    /// build that was staged from one - no pipeline of its own, the tag recorded on it -
    /// and nothing else. See <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    [Fact]
    public async Task ReleaseBuildNowAsync_accepts_a_build_staged_from_a_github_release()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
        var staged = await SeedStagedBuildAsync(ctx, seed.ProjectId, "v1.0.0.0", new[] { "CRONUS Core" });

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, staged);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId);
        delivery.ProjectBuildId.Should().Be(staged);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_staged_build_on_a_pipeline_that_releases_builds()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var staged = await SeedStagedBuildAsync(ctx, seed.ProjectId, "v1.0.0.0", new[] { "CRONUS Core" });

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, staged);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_pipeline_build_on_a_release_sourced_pipeline()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Build"].Should().Contain("GitHub releases");
    }

    [Fact]
    public async Task RunDeliveryAsync_publishes_all_apps_in_order_and_marks_deployed()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "succeeded";
        _apps.StatusByApp["CRONUS Reports"] = "succeeded";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        delivery.ClaimedAt.Should().NotBeNull();
        delivery.StartedAt.Should().NotBeNull();
        delivery.FinishedAt.Should().NotBeNull();
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Completed);
        delivery.Results.Should().OnlyContain(r => r.OperationId != null, "the operation id is what the poll and the admin center key on");
        delivery.Results.Should().OnlyContain(r => r.AppId != null, "BC reads the app id out of the uploaded package");
        // One upload triggered per app, in dependency (stored) order.
        _apps.UploadedOrder.Should().Equal("CRONUS Core", "CRONUS Sales", "CRONUS Reports");
    }

    [Fact]
    public async Task RunDeliveryAsync_marks_failed_and_skips_remaining_when_an_install_fails()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "failed";
        _apps.StatusByApp["CRONUS Reports"] = "succeeded";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("CRONUS Sales");
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Completed);
        results[1].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[2].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        // The failed app's dependent was never triggered.
        _apps.UploadedOrder.Should().Equal("CRONUS Core", "CRONUS Sales");
    }

    [Fact]
    public async Task RunDeliveryAsync_fails_cleanly_when_the_token_cannot_be_acquired()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _tokens.Throw = new BcApiException(null, "The Business Central client secret has expired. Rotate it in Entra and re-enter it before releasing.");

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("expired");
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
        _apps.UploadedOrder.Should().BeEmpty(); // never reached the publish
    }

    [Fact]
    public async Task RunDeliveryAsync_is_a_noop_when_the_delivery_is_not_scheduled()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        // Simulate another worker already past the scheduled state.
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Deployed));

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        _apps.UploadedOrder.Should().BeEmpty(); // the claim CAS found it already taken
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status
            .Should().Be(ProjectDeliveryStatus.Deployed);
    }

    [Fact]
    public async Task ListDeliveryHistoryAsync_returns_deliveries_with_their_app_rows()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "succeeded";
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var runCtx = _db.NewContext()) await NewService(runCtx).RunDeliveryAsync(deliveryId);

        var history = await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId);

        var row = history.Should().ContainSingle().Subject;
        row.Id.Should().Be(deliveryId);
        row.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        row.IsLive.Should().BeFalse();
        row.Apps.Select(a => a.AppName).Should().Equal("CRONUS Core", "CRONUS Sales");
        row.Apps.Should().OnlyContain(a => a.Status == ProjectDeliveryResultStatus.Completed);
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_for_a_future_time_stays_scheduled_and_is_not_enqueued()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });

        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(3));

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status
            .Should().Be(ProjectDeliveryStatus.Scheduled);
        _queue.Reader.TryRead(out _).Should().BeFalse("a future delivery is left for the scheduler, not enqueued now");
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_due_now_is_enqueued_immediately()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });

        await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow);

        _queue.Reader.TryRead(out _).Should().BeTrue("a release due now is enqueued straight away");
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_flags_a_time_outside_the_window()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await SetWindowAsync(ctx, seed.EnvironmentId, new TimeOnly(22, 0), new TimeOnly(6, 0)); // UTC project tz

        // 12:00 UTC tomorrow is outside a 22:00–06:00 window.
        var outside = new DateTime(DateTime.UtcNow.Year, 1, 2, 12, 0, 0, DateTimeKind.Utc).AddYears(1);
        var insideId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId,
            new DateTime(outside.Year, outside.Month, outside.Day, 23, 0, 0, DateTimeKind.Utc));
        var outsideId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, outside);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == insideId)).ScheduledOutsideWindow.Should().BeFalse();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == outsideId)).ScheduledOutsideWindow.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueDueDeliveriesAsync_enqueues_due_rows_and_skips_future_ones()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var svc = NewService(ctx);
        await svc.ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        await svc.ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(5));
        DrainQueue();

        // Sweep at now+2h: the first is due, the second isn't.
        var enqueued = await NewService(_db.NewContext()).EnqueueDueDeliveriesAsync(DateTime.UtcNow.AddHours(2));

        enqueued.Should().Be(1);
    }

    [Fact]
    public async Task FailInterruptedDeliveriesAsync_fails_orphaned_in_progress_runs()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        // Simulate a crash mid-publish.
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Uploading));

        var failed = await NewService(_db.NewContext()).FailInterruptedDeliveriesAsync();

        failed.Should().Be(1);
        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.Include(x => x.Results).SingleAsync(x => x.Id == deliveryId);
        d.Status.Should().Be(ProjectDeliveryStatus.Failed);
        d.FailureMessage.Should().Contain("interrupted");
        d.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
    }

    [Fact]
    public async Task CancelDeliveryAsync_cancels_a_scheduled_delivery_but_refuses_a_claimed_one()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var scheduledId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        await NewService(_db.NewContext()).CancelDeliveryAsync(scheduledId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == scheduledId)).Status
            .Should().Be(ProjectDeliveryStatus.Cancelled);

        // A claimed delivery can no longer be cancelled.
        var claimedId = await NewService(_db.NewContext()).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        await ctx.OeProjectDeliveries.Where(d => d.Id == claimedId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed));
        var act = () => NewService(_db.NewContext()).CancelDeliveryAsync(claimedId);
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Delivery");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_moves_a_scheduled_delivery()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        var newTime = DateTime.UtcNow.AddHours(8);

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, newTime);

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        d.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        d.ScheduledFor.Should().BeCloseTo(newTime, TimeSpan.FromSeconds(1));
    }

    // ── Deferred installs: Business Central takes over ─────────────────────────

    [Fact]
    public async Task RunDeliveryAsync_hands_a_deferred_install_to_bc_instead_of_waiting_for_it()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        // The app is already there, so BC accepts a deferred schedule for it.
        _apps.Installed.Add(InstalledApp("CRONUS Core"));

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        ProjectDeliveryStatus.IsTerminal(delivery.Status).Should().BeTrue(
            "nothing further happens on our side once BC has scheduled it");
        delivery.FinishedAt.Should().NotBeNull();
        delivery.FailureMessage.Should().BeNull();
        var result = delivery.Results.Single();
        result.Status.Should().Be(ProjectDeliveryResultStatus.Scheduled);
        result.OperationId.Should().NotBeNull("cancelling it in BC later needs the operation");
        _apps.UploadedOrder.Should().Equal("CRONUS Core");
        _apps.LastSchedule.Should().Be(BcDeploymentSchedule.NextMinorUpdate);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_several_apps_on_a_deferred_schedule()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" },
            deploymentSchedule: BcDeploymentSchedule.NextMajorUpdate);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["DeploymentSchedule"];
        error.Should().Contain("order",
            "BC picks the install order inside its own window, so our dependency order stops meaning anything");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RunDeliveryAsync_refuses_a_deferred_first_install_of_an_app_bc_has_never_seen()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        // Nothing installed: this is the app's first visit to the environment.

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("CRONUS Core").And.Contain("isn't installed");
        _apps.UploadedOrder.Should().BeEmpty("the rule is checked before anything is uploaded");
    }

    // ── Legacy values from the retired upload API ─────────────────────────────

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_a_pipeline_still_holding_the_old_version_wording()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: "Current Version");

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DeploymentSchedule");
        _apps.UploadedOrder.Should().BeEmpty("the data migration is required, not optional");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_a_pipeline_still_holding_the_spaced_force_sync()
    {
        await using var ctx = _db.NewContext();
        // The App Management API spells it "ForceSync"; the old one had a space.
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, schemaSyncMode: "Force Sync");

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("SchemaSyncMode");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    // ── What actually goes over the wire ──────────────────────────────────────

    [Fact]
    public async Task RunDeliveryAsync_uploads_with_dependency_resolution_on_and_no_language()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ApplicationFamily, "BusinessCentral"));

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        _apps.LastInstallDependencies.Should().BeTrue(
            "the API defaults it to false, and BC can resolve dependencies it can already see");
        _apps.LastLanguageId.Should().BeEmpty(
            "we have no language concept, and guessing one would set the install locale wrong");
        _apps.LastFamily.Should().Be("BusinessCentral", "the family is whatever the API called it");
        _apps.LastSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task RunDeliveryAsync_records_the_failure_codes_rather_than_the_localized_message()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _apps.StatusByApp["CRONUS Core"] = "failed";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        // The codes are the part that means the same thing in every tenant's language.
        delivery.Results.Single().Message.Should()
            .Contain("ExtensionChangeFailed").And.Contain("TenantSyncFailure");
    }

    private static BcInstalledApp InstalledApp(string name) => new(
        AppId: Guid.NewGuid(), Name: name, Publisher: "CRONUS A/S", Version: "1.0.0.0",
        State: "Installed", AppType: "tenant", CanBeUninstalled: true,
        LastOperationId: null, LastUpdateAttemptResult: string.Empty);

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
    }

    private static async Task SetWindowAsync(AppDbContext ctx, int environmentId, TimeOnly start, TimeOnly end)
    {
        await ctx.OeProjectEnvironments.Where(e => e.Id == environmentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.UpdateWindowStart, start)
                .SetProperty(e => e.UpdateWindowEnd, end));
    }

    private DeliveryService NewService(AppDbContext ctx)
    {
        var svc = new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            _tokens, _apps, _admin, _queue,
            new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System),
            NullLogger<DeliveryService>.Instance)
        {
            PollDelay = TimeSpan.Zero,
            PollTimeoutPerApp = TimeSpan.FromSeconds(5),
        };
        return svc;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int BuildPipelineId, int EnvironmentId, int ReleasePipelineId, int BuildId);

    private static async Task<Seed> SeedAsync(AppDbContext ctx, string[] appNames,
        string buildStatus = ProjectBuildStatus.Ready,
        string? deploymentSchedule = null,
        string? schemaSyncMode = null)
    {
        var now = DateTime.UtcNow;
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipelineId = await SeedPipelineAsync(ctx, project.Id);
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipelineId, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = deploymentSchedule ?? BcDeploymentSchedule.Immediate,
            SchemaSyncMode = schemaSyncMode ?? BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var buildId = await SeedBuildAsync(ctx, project.Id, pipelineId, buildStatus, appNames);
        return new Seed(project.Id, pipelineId, env.Id, releasePipeline.Id, buildId);
    }

    /// <summary>Points a seeded release pipeline at a repository's GitHub releases instead of a build pipeline.</summary>
    private static async Task MakeReleaseSourcedAsync(AppDbContext ctx, int releasePipelineId)
    {
        var rp = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId);
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = rp.ProjectId,
            Provider = RepositoryProvider.GitHub, Url = "https://github.com/cronus-dk/cronus-customer.git",
            DisplayName = "cronus-customer",
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();

        rp.ArtifactSource = ReleaseArtifactSource.GithubRelease;
        rp.BuildPipelineId = null;
        rp.GithubReleaseRepositoryId = repository.Id;
        await ctx.SaveChangesAsync();
    }

    /// <summary>A build staged from a GitHub release: ready, no pipeline, the tag recorded.</summary>
    private static async Task<int> SeedStagedBuildAsync(AppDbContext ctx, int projectId, string tag, string[] appNames)
    {
        var now = DateTime.UtcNow;
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = null,
            Status = ProjectBuildStatus.Ready, GithubReleaseTag = tag,
            GithubReleaseUrl = $"https://github.com/cronus-dk/cronus-customer/releases/tag/{tag}",
            StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        foreach (var name in appNames)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"{name}_1.0.0.0.app", AppName = name, AppVersion = "1.0.0.0",
                SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private static async Task<int> SeedPipelineAsync(AppDbContext ctx, int projectId)
    {
        var p = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build " + Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.OePipelines.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> SeedBuildAsync(AppDbContext ctx, int projectId, int pipelineId, string status, string[] appNames)
    {
        var now = DateTime.UtcNow;
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = status, StartedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        // Artifacts inserted in dependency order (the build's TopologicalOrder), preserved by id.
        for (var i = 0; i < appNames.Length; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"{appNames[i]}_1.0.{i}.0.app", AppName = appNames[i], AppVersion = $"1.0.{i}.0",
                SizeBytes = 10 + i, Content = new byte[] { 1, 2, 3, (byte)i }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeTokenSource : IDeliveryTokenSource
    {
        public string Token = "fake-token";
        public Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Exception? Throw;
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => Throw is not null ? throw Throw : Task.FromResult(new BcDeliveryContext(Token, TenantId));
    }

    /// <summary>
    /// The claim-time environment re-read. Defaults to an Active environment so the
    /// existing publish tests are unaffected; a test that cares sets <see cref="OnGet"/>.
    /// </summary>
    private sealed class FakeAdminClient : IBcAdminClient
    {
        public Func<string, BcEnvironment?> OnGet = name => new BcEnvironment(name, "Production") { Status = "Active" };
        public List<string> Requested { get; } = new();

        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<BcEnvironment>)Array.Empty<BcEnvironment>());

        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Requested.Add(environmentName);
            return Task.FromResult(OnGet(environmentName));
        }

        // The delivery flow never reads or writes Microsoft's update window - that is
        // context on the project page, not an input to a publish.
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The App Management surface. Uploads are recorded in order and answered with an
    /// operation whose ids the run is expected to keep; the poll then reports whatever
    /// <see cref="StatusByApp"/> says for that app. Statuses come back in the upload
    /// endpoint's lowercase spelling, which is not the casing the operations endpoint
    /// uses - that difference is exactly what the run must not depend on.
    /// </summary>
    private sealed class FakeAppManagementClient : IBcAppManagementClient
    {
        /// <summary>App name to the status its install operation reports. Missing = "succeeded".</summary>
        public Dictionary<string, string> StatusByApp { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>App names in upload order, so a test can assert dependency order was kept.</summary>
        public List<string> UploadedOrder { get; } = new();

        /// <summary>What the environment already has installed. Empty = every app is new to it.</summary>
        public List<BcInstalledApp> Installed { get; } = new();

        /// <summary>Set to fail the installed-apps read.</summary>
        public BcApiException? ListThrows;

        /// <summary>What the last upload was sent with, for the tests that pin the call.</summary>
        public string? LastSchedule;
        public string? LastSyncMode;
        public string? LastLanguageId;
        public string? LastFamily;
        public bool LastInstallDependencies;

        private readonly Dictionary<Guid, string> _appNameByAppId = new();

        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(
            string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        {
            LastFamily = applicationFamily;
            if (ListThrows is not null) throw ListThrows;
            return Task.FromResult((IReadOnlyList<BcInstalledApp>)Installed);
        }

        public Task<BcAppOperation> InstallPteAsync(
            string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName,
            string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies,
            CancellationToken ct = default)
        {
            // The seed names artifacts "<App Name>_<version>.app".
            var appName = fileName[..fileName.LastIndexOf('_')];
            UploadedOrder.Add(appName);
            LastSchedule = deploymentSchedule;
            LastSyncMode = syncMode;
            LastLanguageId = languageId;
            LastInstallDependencies = installOrUpdateNeededDependencies;

            var appId = Guid.NewGuid();
            _appNameByAppId[appId] = appName;
            var status = BcDeploymentSchedule.IsDeferred(deploymentSchedule) ? "scheduled" : "running";
            return Task.FromResult(Operation(appId, status));
        }

        public Task<BcAppOperation?> GetAppOperationAsync(
            string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId,
            CancellationToken ct = default)
        {
            var name = _appNameByAppId.GetValueOrDefault(appId, string.Empty);
            var status = StatusByApp.GetValueOrDefault(name, "succeeded");
            return Task.FromResult<BcAppOperation?>(Operation(appId, status, operationId));
        }

        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(
            string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<BcScheduledPteOperation>)Array.Empty<BcScheduledPteOperation>());
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(
            string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion,
            string scheduleKind, CancellationToken ct = default)
            => Task.FromResult(Operation(appId, "canceled"));

        private static BcAppOperation Operation(Guid appId, string status, Guid? operationId = null) => new(
            Id: operationId ?? Guid.NewGuid(),
            AppId: appId,
            Type: "install",
            Status: BcAppManagementClient.ParseStatus(status),
            RawStatus: status,
            SourceAppVersion: string.Empty,
            TargetAppVersion: string.Empty,
            ScheduleKind: null,
            // A real failure comes back in the environment's language; nothing may read it.
            ErrorMessage: status == "failed" ? "Installationen af udvidelsen mislykkedes." : string.Empty,
            ErrorCode: status == "failed" ? "ExtensionChangeFailed" : string.Empty,
            InnerErrorCode: status == "failed" ? "TenantSyncFailure" : string.Empty,
            CanBeCanceled: false,
            CreatorPrincipalType: "app",
            CreatedOn: DateTimeOffset.UtcNow,
            StartedOn: null,
            CompletedOn: null);
    }
}
