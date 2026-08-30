using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP delivery surface — the agent-facing parallel of the Releases web tool.
/// Pins release-pipeline listing, the publish_build → delivery-id contract (delegating
/// to DeliveryService), the unknown-pipeline guard, and the access-denied → McpException
/// translation. The publish here only enqueues (no worker runs), so the BC seams are
/// never exercised. See .design/saas-delivery.md ("MCP parity").
/// </summary>
public sealed class DeliveryToolsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DeliveryQueue _queue = new();

    public DeliveryToolsTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    private DeliveryTools NewTools(AppDbContext ctx) =>
        new(new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                new ThrowingTokenSource(), new ThrowingAppManagementClient(), new ThrowingAdminClient(), _queue,
                NullLogger<DeliveryService>.Instance),
            new ReleasePipelineService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                NullLogger<ReleasePipelineService>.Instance),
            new ProjectAccess(ctx, _db.OrgContext),
            ctx);

    [Fact]
    public async Task List_release_pipelines_returns_the_orgs_pipelines()
    {
        await using (var ctx = _db.NewContext())
        {
            await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        await using var read = _db.NewContext();
        var rows = await NewTools(read).ListReleasePipelinesAsync();

        rows.Should().ContainSingle(r => r.Name == "CRONUS App → Production" && r.EnvironmentName == "Production");
    }

    [Fact]
    public async Task Publish_build_queues_a_delivery_and_returns_its_id()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core", "CRONUS Sales" });
        }

        await using var read = _db.NewContext();
        var result = await NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        result.DeliveryId.Should().BeGreaterThan(0);

        await using var verify = _db.NewContext();
        var delivery = await verify.OeProjectDeliveries.AsNoTracking()
            .SingleAsync(d => d.Id == result.DeliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.ReleasePipelineId.Should().Be(seed.ReleasePipelineId);
    }

    [Fact]
    public async Task Publish_build_surfaces_validation_as_mcp_exception()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            // A failed build can't be released — DeliveryService throws PlanValidationException.
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" }, buildStatus: ProjectBuildStatus.Failed);
        }

        await using var read = _db.NewContext();
        var act = () => NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("Couldn't release");
    }

    [Fact]
    public async Task Publish_build_surfaces_access_denied_as_mcp_exception()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        // A non-owner, non-admin user can't release this ownerless project's builds.
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = 999;

        await using var read = _db.NewContext();
        var act = () => NewTools(read).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("permission");
    }

    [Fact]
    public async Task List_deliveries_throws_for_an_unknown_pipeline()
    {
        await using var read = _db.NewContext();
        var act = () => NewTools(read).ListDeliveriesAsync(999999);

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task List_deliveries_returns_a_published_deliverys_history()
    {
        Seed seed;
        int deliveryId;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
            deliveryId = (await NewTools(ctx).PublishBuildAsync(seed.ReleasePipelineId, seed.BuildId)).DeliveryId;
        }

        await using var read = _db.NewContext();
        var rows = await NewTools(read).ListDeliveriesAsync(seed.ReleasePipelineId);

        rows.Should().ContainSingle(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled);
        rows[0].Apps.Should().ContainSingle(a => a.AppName == "CRONUS Core");
    }

    // ── Project visibility (slice 3) ─────────────────────────────────────

    /// <summary>
    /// A release pipeline inherits its project's visibility. Both the unfiltered
    /// list and the by-id resolve answer a non-member the same way an id in
    /// another org would — absent, then "not found" — never a distinct refusal.
    /// </summary>
    [Fact]
    public async Task A_private_projects_release_pipeline_is_invisible_to_a_non_member()
    {
        Seed seed;
        await using (var ctx = _db.NewContext())
        {
            seed = await SeedAsync(ctx, new[] { "CRONUS Core" });
        }

        const int ownerId = 9700;
        const int memberId = 9701;
        const int outsiderId = 9702;
        int teamId;
        await using (var ctx = _db.NewContext())
        {
            foreach (var (id, email) in new[] { (ownerId, "owner@example.com"), (memberId, "mel@example.com"), (outsiderId, "nils@example.com") })
            {
                ctx.Users.Add(new ALDevToolbox.Domain.Entities.User
                {
                    Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x",
                    DisplayName = email, Role = ALDevToolbox.Domain.Entities.UserRole.User,
                    Status = ALDevToolbox.Domain.Entities.UserStatus.Active, CreatedAt = DateTime.UtcNow,
                });
            }
            var team = new ALDevToolbox.Domain.Entities.Team
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "NDA team",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            teamId = team.Id;
            ctx.TeamMembers.Add(new ALDevToolbox.Domain.Entities.TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = memberId, CreatedAt = DateTime.UtcNow,
            });
            var project = await ctx.OeProjects.FirstAsync(p => p.Id == seed.ProjectId);
            project.CreatedByUserId = ownerId;
            await ctx.SaveChangesAsync();
        }

        // Set it Private as a SiteAdmin (the fixture's default identity).
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(seed.ProjectId, ProjectVisibility.Private, new[] { teamId });
        }

        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = outsiderId;
        await using (var read = _db.NewContext())
        {
            var tools = NewTools(read);
            (await tools.ListReleasePipelinesAsync()).Should().NotContain(r => r.Id == seed.ReleasePipelineId);
            (await ((Func<Task>)(() => tools.ListDeliveriesAsync(seed.ReleasePipelineId)))
                .Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
            await ((Func<Task>)(() => tools.ListReleasePipelinesAsync(seed.ProjectId)))
                .Should().ThrowAsync<McpException>();
        }

        _db.OrgContext.CurrentUserId = memberId;
        await using (var read = _db.NewContext())
        {
            var tools = NewTools(read);
            (await tools.ListReleasePipelinesAsync()).Should().ContainSingle(r => r.Id == seed.ReleasePipelineId);
            (await tools.ListDeliveriesAsync(seed.ReleasePipelineId)).Should().BeEmpty();
        }
    }

    // ── Seeding (a project → build pipeline → environment → release pipeline → build) ──

    private sealed record Seed(int ProjectId, int ReleasePipelineId, int BuildId);

    private static async Task<Seed> SeedAsync(AppDbContext ctx, string[] appNames, string buildStatus = ProjectBuildStatus.Ready)
    {
        var now = DateTime.UtcNow;
        var project = new Project { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipeline = new Pipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        var env = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new ReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var build = new ProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = buildStatus, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < appNames.Length; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new ProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = appNames[i] + ".app", AppName = appNames[i], AppVersion = "1.0.0.0",
                Content = new byte[] { 1, 2, 3 }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();

        return new Seed(project.Id, releasePipeline.Id, build.Id);
    }

    // The publish here only enqueues; these seams are never reached in these tests.
    private sealed class ThrowingTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised: publish_build tests only enqueue.");
    }

    private sealed class ThrowingAdminClient : IBcAdminClient
    {
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>These tools read history and enqueue; none of them talks to BC.</summary>
    private sealed class ThrowingAppManagementClient : IBcAppManagementClient
    {
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
