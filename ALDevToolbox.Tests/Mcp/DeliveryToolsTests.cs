using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
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
                new ThrowingTokenSource(), new ThrowingAutomationClient(), _queue,
                NullLogger<DeliveryService>.Instance),
            new ReleasePipelineService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
                NullLogger<ReleasePipelineService>.Instance),
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
            CompanyId = Guid.NewGuid(), CompanyName = "CRONUS International Ltd.", FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new ReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            VersionMode = ReleaseVersionMode.CurrentVersion, SchemaSyncMode = SchemaSyncMode.Add,
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
        public Task<string> AcquireDeliveryTokenAsync(int projectId, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised: publish_build tests only enqueue.");
    }

    private sealed class ThrowingAutomationClient : IBcAutomationClient
    {
        public Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(string accessToken, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcExtensionUpload> CreateExtensionUploadAsync(string accessToken, string environmentName, Guid companyId, string schedule, string schemaSyncMode, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetExtensionContentAsync(string accessToken, string environmentName, Guid companyId, string uploadSystemId, byte[] appBytes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task TriggerExtensionUploadAsync(string accessToken, string environmentName, Guid companyId, string uploadSystemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcDeploymentStatus>> GetDeploymentStatusAsync(string accessToken, string environmentName, Guid companyId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
