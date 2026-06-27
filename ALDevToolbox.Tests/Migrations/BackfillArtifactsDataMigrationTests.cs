using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the Artifacts data backfill (slice 4). The fixture's MigrateAsync already
/// ran the migration against an empty DB (a no-op), so this test seeds the
/// pre-Artifacts legacy shape — an ownerless project with repos, a project-kind
/// release reachable via an import job, and the old per-app build report — then
/// replays <see cref="BackfillArtifactsData.BackfillSql"/> and asserts the
/// migrated shape: the project gets an owner, a ProjectBuild is synthesised and
/// linked to its release, the report's provenance lands on the build's commit set,
/// and the org's allowed-providers set is seeded from its repos. See
/// <c>.design/artifacts.md</c>.
/// </summary>
/// <remarks>
/// The SQL is the contract; this calls <see cref="BackfillArtifactsData.BackfillSql"/>
/// rather than copy-pasting so a regression in the migration body trips the test.
/// </remarks>
public sealed class BackfillArtifactsDataMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Replaying_the_backfill_owns_synthesises_migrates_and_seeds_providers()
    {
        const int org = TestDb.DefaultOrgId;
        int adminId, projectId, releaseId;

        await using (var seed = _db.NewContext())
        {
            // An active Admin to adopt the ownerless project.
            var admin = new User
            {
                OrganizationId = org, Email = "admin@cronus.example", PasswordHash = "x",
                DisplayName = "Admin", Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            };
            seed.Users.Add(admin);
            await seed.SaveChangesAsync();
            adminId = admin.Id;

            // An ownerless project (created_by_user_id null) with two repos on
            // different providers.
            var project = new Project
            {
                OrganizationId = org, Name = "CRONUS A/S", CreatedByUserId = null,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Repositories =
                {
                    new ProjectRepository { OrganizationId = org, Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub, Url = "https://github.com/cronus/core", DisplayName = "core" },
                    new ProjectRepository { OrganizationId = org, Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.AzureDevOps, Url = "https://dev.azure.com/cronus/bc/_git/exts", DisplayName = "exts" },
                },
            };
            seed.OeProjects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;

            // A project-kind release this project produced, reachable via an import job.
            var release = new Release
            {
                OrganizationId = org, Label = "CRONUS A/S on BC 26.0", Kind = "project", Status = "ready",
                BcVersion = "26.0", ImportedAt = new DateTime(2026, 6, 20, 9, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(release);
            await seed.SaveChangesAsync();
            releaseId = release.Id;

            seed.OeImportJobs.Add(new ImportJob
            {
                OrganizationId = org, ReleaseId = releaseId, ProjectId = projectId,
                Kind = "project_build", Status = "completed", CreatedAt = DateTime.UtcNow,
            });
            // The legacy per-app build report carrying source provenance — two apps
            // from the same repo+commit collapse to one commit row.
            seed.OeProjectBuildResults.AddRange(
                new ProjectBuildResult
                {
                    OrganizationId = org, ReleaseId = releaseId, AppName = "Core", AppId = Guid.NewGuid().ToString(),
                    Status = ProjectBuildResultStatus.Ingested, RepoUrl = "https://github.com/cronus/core.git",
                    CommitSha = "9a1f2b3c4d5e", CommitDate = new DateTime(2026, 6, 20, 8, 0, 0, DateTimeKind.Utc), CreatedAt = DateTime.UtcNow,
                },
                new ProjectBuildResult
                {
                    OrganizationId = org, ReleaseId = releaseId, AppName = "Extra", AppId = Guid.NewGuid().ToString(),
                    Status = ProjectBuildResultStatus.Ingested, RepoUrl = "https://github.com/cronus/core.git",
                    CommitSha = "9a1f2b3c4d5e", CommitDate = new DateTime(2026, 6, 20, 8, 0, 0, DateTimeKind.Utc), CreatedAt = DateTime.UtcNow,
                });

            // An org settings row with an empty allowed-providers set.
            var settings = await seed.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == org);
            if (settings is null)
            {
                seed.OrganizationSettings.Add(new OrganizationSettings { OrganizationId = org });
            }
            else
            {
                settings.AllowedRepositoryProviders = new();
            }
            await seed.SaveChangesAsync();
        }

        // Replay the migration's SQL (idempotent).
        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync(BackfillArtifactsData.BackfillSql);
            // A second run must be a no-op (idempotency).
            await run.Database.ExecuteSqlRawAsync(BackfillArtifactsData.BackfillSql);
        }

        await using var read = _db.NewContext();

        (await read.OeProjects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.CreatedByUserId).SingleAsync())
            .Should().Be(adminId, "the ownerless project is adopted by the org's active Admin");

        var builds = await read.OeProjectBuilds.AsNoTracking().Where(b => b.ReleaseId == releaseId).ToListAsync();
        builds.Should().ContainSingle("exactly one ProjectBuild is synthesised per release, even across two runs");
        builds[0].Status.Should().Be(ProjectBuildStatus.Ready);
        builds[0].ProjectId.Should().Be(projectId);
        builds[0].BcVersion.Should().Be("26.0");

        var commits = await read.OeProjectBuildRepoCommits.AsNoTracking().Where(c => c.ProjectBuildId == builds[0].Id).ToListAsync();
        commits.Should().ContainSingle("the two apps share one repo+commit, so the provenance collapses to one row");
        commits[0].CommitHash.Should().Be("9a1f2b3c4d5e");
        commits[0].RepoDisplayName.Should().Be("core", "the display name is the URL's last segment without .git");

        var providers = await read.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => s.AllowedRepositoryProviders).SingleAsync();
        providers.Should().BeEquivalentTo(new[] { "azure_devops", "github" },
            "the allowed set is seeded from the providers the project's repos use");
    }
}
