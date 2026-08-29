using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// CRUD + validation for <see cref="ReleasePipelineService"/> against the shared
/// <see cref="TestDb"/> fixture: name required and unique per project (free to repeat
/// across projects), the source build pipeline and target environment must belong to
/// the same project, the environment must have a company picked, the version /
/// schema-sync modes are validated, plus update, soft-delete, and the list
/// projection. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ReleasePipelineServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ReleasePipelineServiceTests()
    {
        // Manage rights come from the parent project's owner; act as SiteAdmin so the
        // access gate passes without seeding a user.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateReleasePipelineAsync_persists_all_fields_with_mode_defaults()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId,
            BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Contoso → Production");
        rp.ProjectId.Should().Be(projectId);
        rp.BuildPipelineId.Should().Be(buildId);
        rp.ProjectEnvironmentId.Should().Be(envId);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMinorUpdate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_defaults_blank_modes_to_current_version_and_add()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "", ""));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_requires_a_name()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "  ", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var buildA = await SeedBuildPipelineAsync(ctx, projectA);
        var envA = await SeedEnvironmentAsync(ctx, projectA);
        var buildB = await SeedBuildPipelineAsync(ctx, projectB);
        var envB = await SeedEnvironmentAsync(ctx, projectB);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectA, "Production", buildA, envA, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectB, "Production", buildB, envB, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(424242, "Rel", 1, 1, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_build_pipeline_from_another_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var otherBuild = await SeedBuildPipelineAsync(ctx, projectB);
        var envId = await SeedEnvironmentAsync(ctx, projectA);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectA, "Rel", otherBuild, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("BuildPipelineId");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_that_is_no_longer_in_business_central()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Retired", missing: true);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("Retired", "the refusal names the environment the consultant picked");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_that_cannot_take_an_install()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production", status: "Upgrading");

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("Upgrading", "the same wording the delivery gate uses, just earlier");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_accepts_an_environment_with_no_status_yet()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        // Rows fetched before the status was captured have none; that must not block.
        var envId = await SeedEnvironmentAsync(ctx, projectId, status: null);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_unknown_version_mode()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "Whenever", BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("DeploymentSchedule");
    }

    [Fact]
    public async Task UpdateReleasePipelineAsync_changes_target_and_modes()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var prodEnv = await SeedEnvironmentAsync(ctx, projectId);
        var sandboxEnv = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, prodEnv, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Sandbox", buildId, sandboxEnv, BcDeploymentSchedule.NextMajorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Sandbox");
        rp.ProjectEnvironmentId.Should().Be(sandboxEnv);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMajorUpdate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    [Fact]
    public async Task SoftDeleteReleasePipelineAsync_hides_the_release_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await svc.SoftDeleteReleasePipelineAsync(id);

        await using var read = _db.NewContext();
        (await NewService(read).GetReleasePipelineAsync(id)).Should().BeNull();
        (await read.OeReleasePipelines.IgnoreQueryFilters().SingleAsync(r => r.Id == id)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ListReleasePipelinesAsync_resolves_source_and_target_names()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, name: "Nightly");
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production");
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var rows = await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.BuildPipelineName.Should().Be("Nightly");
        row.EnvironmentName.Should().Be("Production");
        row.EnvironmentMissing.Should().BeFalse();
    }

    private ReleasePipelineService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleasePipelineService>.Instance);

    private static async Task<int> SeedProjectAsync(AppDbContext ctx)
    {
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedBuildPipelineAsync(AppDbContext ctx, int projectId, string? name = null)
    {
        var pipeline = new Pipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Build " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedEnvironmentAsync(
        AppDbContext ctx, int projectId, string? name = null, string? status = null, bool missing = false)
    {
        var env = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Env " + Guid.NewGuid().ToString("N"),
            Type = "Production",
            Status = status,
            MissingSince = missing ? DateTime.UtcNow : null,
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }
}
