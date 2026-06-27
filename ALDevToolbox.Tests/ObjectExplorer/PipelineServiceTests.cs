using System.Text.Json;
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
/// CRUD + validation for <see cref="PipelineService"/> against the shared
/// <see cref="TestDb"/> fixture: name required and unique per project (but free to
/// repeat across projects), the selection serialised to JSON (null = build all),
/// update, and soft-delete. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class PipelineServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public PipelineServiceTests()
    {
        // Manage rights come from the parent project's owner; act as SiteAdmin so the
        // access gate passes without seeding a user.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreatePipelineAsync_persists_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var selection = new[] { "11111111-1111-1111-1111-111111111111" };

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", selection));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Production");
        pipeline.ProjectId.Should().Be(projectId);
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal(selection);
    }

    [Fact]
    public async Task CreatePipelineAsync_stores_null_selection_for_build_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "All", SelectedAppIds: null));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).RequestedAppIdsJson.Should().BeNull();
    }

    [Fact]
    public async Task CreatePipelineAsync_requires_a_name()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "  ", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectId, "production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreatePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectA, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectB, "Production", null));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(424242, "Production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task UpdatePipelineAsync_changes_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Test", new[] { "aaa" }));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Test");
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal("aaa");
    }

    [Fact]
    public async Task SoftDeletePipelineAsync_hides_the_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.SoftDeletePipelineAsync(id);

        await using var read = _db.NewContext();
        (await NewService(read).GetPipelineAsync(id)).Should().BeNull();
        (await read.OePipelines.IgnoreQueryFilters().SingleAsync(p => p.Id == id)).DeletedAt.Should().NotBeNull();
    }

    private PipelineService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<PipelineService>.Instance);

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
}
