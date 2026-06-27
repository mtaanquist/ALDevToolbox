using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the Project -> Pipeline -> Build backfill (the AddPipelines migration). The
/// fixture's MigrateAsync already ran it against an empty DB (a no-op), so this test
/// seeds a project with a pipeline-less build, replays
/// <see cref="AddPipelines.BackfillSql"/>, and asserts the migrated shape: a single
/// "Default" pipeline (build everything) per project, with existing builds re-parented
/// onto it. Idempotent on a second run. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class AddPipelinesBackfillMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Replaying_the_backfill_creates_a_default_pipeline_and_reparents_builds()
    {
        const int org = TestDb.DefaultOrgId;
        int projectId, buildId;

        await using (var seed = _db.NewContext())
        {
            var project = new Project
            {
                OrganizationId = org, Name = "CRONUS A/S " + Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OeProjects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;

            // A pre-Pipeline build hanging directly off the project (pipeline_id null).
            var build = new ProjectBuild
            {
                OrganizationId = org, ProjectId = projectId, PipelineId = null,
                Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
            };
            seed.OeProjectBuilds.Add(build);
            await seed.SaveChangesAsync();
            buildId = build.Id;
        }

        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync(AddPipelines.BackfillSql);
            // A second run must be a no-op (idempotency).
            await run.Database.ExecuteSqlRawAsync(AddPipelines.BackfillSql);
        }

        await using var read = _db.NewContext();

        var pipelines = await read.OePipelines.AsNoTracking().Where(p => p.ProjectId == projectId).ToListAsync();
        pipelines.Should().ContainSingle("one Default pipeline per project, even across two runs");
        pipelines[0].Name.Should().Be("Default");
        pipelines[0].RequestedAppIdsJson.Should().BeNull("the Default pipeline builds everything");

        var reparented = await read.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == buildId);
        reparented.PipelineId.Should().Be(pipelines[0].Id, "existing builds are re-parented onto the Default pipeline");
    }
}
