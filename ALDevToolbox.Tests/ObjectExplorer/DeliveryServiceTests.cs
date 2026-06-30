using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Create + run for <see cref="DeliveryService"/> against the shared <see cref="TestDb"/>,
/// driving the publish orchestration through fake <see cref="IBcAutomationClient"/> and
/// <see cref="IDeliveryTokenSource"/> seams (no real BC). Covers the snapshot at
/// creation, the validation guards, the happy-path upload→install→poll in dependency
/// order, partial failure (fail + skip the rest), a clean token failure, and the
/// claim no-op. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class DeliveryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAutomationClient _automation = new();
    private readonly FakeTokenSource _tokens = new();
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
        delivery.CompanyId.Should().Be(seed.CompanyId);
        delivery.VersionMode.Should().Be(ReleaseVersionMode.CurrentVersion);
        delivery.SchemaSyncMode.Should().Be(SchemaSyncMode.Add);
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

    [Fact]
    public async Task RunDeliveryAsync_publishes_all_apps_in_order_and_marks_deployed()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _automation.StatusByApp["CRONUS Core"] = "Completed";
        _automation.StatusByApp["CRONUS Sales"] = "Completed";
        _automation.StatusByApp["CRONUS Reports"] = "Completed";

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
        delivery.Results.Should().OnlyContain(r => r.ExtensionUploadId != null);
        // One upload triggered per app, in dependency (stored) order.
        _automation.TriggeredOrder.Should().Equal("upload-1", "upload-2", "upload-3");
    }

    [Fact]
    public async Task RunDeliveryAsync_marks_failed_and_skips_remaining_when_an_install_fails()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _automation.StatusByApp["CRONUS Core"] = "Completed";
        _automation.StatusByApp["CRONUS Sales"] = "Failed";
        _automation.StatusByApp["CRONUS Reports"] = "Completed";

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
        _automation.TriggeredOrder.Should().Equal("upload-1", "upload-2");
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
        _automation.TriggeredOrder.Should().BeEmpty(); // never reached the publish
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

        _automation.TriggeredOrder.Should().BeEmpty(); // the claim CAS found it already taken
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status
            .Should().Be(ProjectDeliveryStatus.Deployed);
    }

    [Fact]
    public async Task ListDeliveryHistoryAsync_returns_deliveries_with_their_app_rows()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        _automation.StatusByApp["CRONUS Core"] = "Completed";
        _automation.StatusByApp["CRONUS Sales"] = "Completed";
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
            _tokens, _automation, _queue, NullLogger<DeliveryService>.Instance)
        {
            PollDelay = TimeSpan.Zero,
            PollTimeoutPerApp = TimeSpan.FromSeconds(5),
        };
        return svc;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int BuildPipelineId, int EnvironmentId, Guid CompanyId, int ReleasePipelineId, int BuildId);

    private static async Task<Seed> SeedAsync(AppDbContext ctx, string[] appNames,
        string buildStatus = ProjectBuildStatus.Ready)
    {
        var now = DateTime.UtcNow;
        var project = new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipelineId = await SeedPipelineAsync(ctx, project.Id);
        var companyId = Guid.NewGuid();
        var env = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            CompanyId = companyId, CompanyName = "CRONUS International Ltd.", FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new ReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipelineId, ProjectEnvironmentId = env.Id,
            VersionMode = ReleaseVersionMode.CurrentVersion, SchemaSyncMode = SchemaSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var buildId = await SeedBuildAsync(ctx, project.Id, pipelineId, buildStatus, appNames);
        return new Seed(project.Id, pipelineId, env.Id, companyId, releasePipeline.Id, buildId);
    }

    private static async Task<int> SeedPipelineAsync(AppDbContext ctx, int projectId)
    {
        var p = new Pipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build " + Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.OePipelines.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> SeedBuildAsync(AppDbContext ctx, int projectId, int pipelineId, string status, string[] appNames)
    {
        var now = DateTime.UtcNow;
        var build = new ProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = status, StartedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        // Artifacts inserted in dependency order (the build's TopologicalOrder), preserved by id.
        for (var i = 0; i < appNames.Length; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new ProjectBuildArtifact
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
        public Exception? Throw;
        public Task<string> AcquireDeliveryTokenAsync(int projectId, CancellationToken ct = default)
            => Throw is not null ? throw Throw : Task.FromResult(Token);
    }

    private sealed class FakeAutomationClient : IBcAutomationClient
    {
        /// <summary>App name → deployment status string the poll will return.</summary>
        public Dictionary<string, string> StatusByApp { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> TriggeredOrder { get; } = new();
        private int _seq;

        public Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(string accessToken, string environmentName, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<BcCompany>)Array.Empty<BcCompany>());

        public Task<BcExtensionUpload> CreateExtensionUploadAsync(string accessToken, string environmentName, Guid companyId, string schedule, string schemaSyncMode, CancellationToken ct = default)
            => Task.FromResult(new BcExtensionUpload($"upload-{++_seq}"));

        public Task SetExtensionContentAsync(string accessToken, string environmentName, Guid companyId, string uploadSystemId, byte[] appBytes, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task TriggerExtensionUploadAsync(string accessToken, string environmentName, Guid companyId, string uploadSystemId, CancellationToken ct = default)
        {
            TriggeredOrder.Add(uploadSystemId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BcDeploymentStatus>> GetDeploymentStatusAsync(string accessToken, string environmentName, Guid companyId, CancellationToken ct = default)
        {
            var rows = StatusByApp.Select(kv => new BcDeploymentStatus(kv.Key, string.Empty, kv.Value)).ToList();
            return Task.FromResult((IReadOnlyList<BcDeploymentStatus>)rows);
        }
    }
}
