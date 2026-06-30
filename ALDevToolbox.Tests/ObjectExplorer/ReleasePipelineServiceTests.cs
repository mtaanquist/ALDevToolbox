using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
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
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId,
            ReleaseVersionMode.NextMinorVersion, SchemaSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Contoso → Production");
        rp.ProjectId.Should().Be(projectId);
        rp.BuildPipelineId.Should().Be(buildId);
        rp.ProjectEnvironmentId.Should().Be(envId);
        rp.VersionMode.Should().Be(ReleaseVersionMode.NextMinorVersion);
        rp.SchemaSyncMode.Should().Be(SchemaSyncMode.ForceSync);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_defaults_blank_modes_to_current_version_and_add()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "", ""));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.VersionMode.Should().Be(ReleaseVersionMode.CurrentVersion);
        rp.SchemaSyncMode.Should().Be(SchemaSyncMode.Add);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_requires_a_name()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "  ", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "Production", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "production", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var buildA = await SeedBuildPipelineAsync(ctx, projectA);
        var envA = await SeedEnvironmentAsync(ctx, projectA, withCompany: true);
        var buildB = await SeedBuildPipelineAsync(ctx, projectB);
        var envB = await SeedEnvironmentAsync(ctx, projectB, withCompany: true);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectA, "Production", buildA, envA, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectB, "Production", buildB, envB, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(424242, "Rel", 1, 1, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_build_pipeline_from_another_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var otherBuild = await SeedBuildPipelineAsync(ctx, projectB);
        var envId = await SeedEnvironmentAsync(ctx, projectA, withCompany: true);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectA, "Rel", otherBuild, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("BuildPipelineId");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_without_a_company_picked()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: false);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ProjectEnvironmentId");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_unknown_version_mode()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "Whenever", SchemaSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("VersionMode");
    }

    [Fact]
    public async Task UpdateReleasePipelineAsync_changes_target_and_modes()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var prodEnv = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);
        var sandboxEnv = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, prodEnv, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Sandbox", buildId, sandboxEnv, ReleaseVersionMode.NextMajorVersion, SchemaSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Sandbox");
        rp.ProjectEnvironmentId.Should().Be(sandboxEnv);
        rp.VersionMode.Should().Be(ReleaseVersionMode.NextMajorVersion);
        rp.SchemaSyncMode.Should().Be(SchemaSyncMode.ForceSync);
    }

    [Fact]
    public async Task SoftDeleteReleasePipelineAsync_hides_the_release_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

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
        var envId = await SeedEnvironmentAsync(ctx, projectId, withCompany: true, name: "Production", company: "CRONUS Inc.");
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId, ReleaseVersionMode.CurrentVersion, SchemaSyncMode.Add));

        var rows = await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.BuildPipelineName.Should().Be("Nightly");
        row.EnvironmentName.Should().Be("Production");
        row.CompanyName.Should().Be("CRONUS Inc.");
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
        AppDbContext ctx, int projectId, bool withCompany, string? name = null, string? company = null)
    {
        var env = new ProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Env " + Guid.NewGuid().ToString("N"),
            Type = "Production",
            CompanyId = withCompany ? Guid.NewGuid() : null,
            CompanyName = withCompany ? (company ?? "CRONUS") : null,
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }
}
