using System.Text;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Artifacts read surface: the project directory with its latest build, build
/// history + detail (commit set, changelog grouped by repo, deliverables, logs),
/// the project-scoped compare picker, and the download byte fetches. All reads are
/// org-scoped by the EF query filter. See .design/artifacts.md.
/// </summary>
public sealed class ArtifactServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ArtifactService Svc(Data.AppDbContext ctx) => new(ctx);

    [Fact]
    public async Task ListProjectsAsync_summarises_the_latest_build_and_latest_successful()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            // An older successful build, then a newer failed one.
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), bcVersion: "26.0", artifactCount: 2);
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc));
        }

        await using var read = _db.NewContext();
        var rows = await Svc(read).ListProjectsAsync();

        var row = rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("CRONUS A/S");
        row.Latest!.Status.Should().Be(ProjectBuildStatus.Failed, "the newest build wins the summary");
        row.LatestSuccessfulBuildId.Should().NotBeNull("the older ready build is the Download-all target");
    }

    [Fact]
    public async Task ListProjectsAsync_includes_the_latest_build_branch_and_representative_commit()
    {
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core", "trade" });
            var buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", branch: "main");
            // Two repos: the cell shows the first by display name ("core"), shortened to 7 chars.
            ctx.OeProjectBuildRepoCommits.AddRange(
                new ProjectBuildRepoCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, RepoUrl = "u", RepoDisplayName = "trade", CommitHash = "9999999bbb" },
                new ProjectBuildRepoCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, RepoUrl = "u", RepoDisplayName = "core", CommitHash = "abc1234def" });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = (await Svc(read).ListProjectsAsync()).Should().ContainSingle().Subject;
        row.Latest!.Branch.Should().Be("main");
        row.Latest.CommitShort.Should().Be("abc1234", "the first repo by display name wins and the hash is shortened to 7 chars");
    }

    [Fact]
    public async Task ListProjectsAsync_filters_by_name_owner_or_repo()
    {
        await using (var ctx = _db.NewContext())
        {
            await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            await SeedProjectAsync(ctx, "Northwind", repoNames: new[] { "widgets" });
        }

        await using var read = _db.NewContext();
        (await Svc(read).ListProjectsAsync("widgets")).Should().ContainSingle(r => r.Name == "Northwind");
        (await Svc(read).ListProjectsAsync("CRONUS")).Should().ContainSingle(r => r.Name == "CRONUS A/S");
    }

    [Fact]
    public async Task GetBuildDetailAsync_groups_changelog_by_repo_and_lists_deliverables()
    {
        int buildId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow, bcVersion: "26.0", artifactCount: 1);
            var repoId = ctx.OeProjectRepositories.First(r => r.ProjectId == projectId).Id;

            ctx.OeProjectBuildRepoCommits.Add(new ProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repoId,
                RepoUrl = "https://github.com/cronus/core", RepoDisplayName = "core", CommitHash = "abc1234",
            });
            ctx.OeProjectBuildCommits.Add(new ProjectBuildCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repoId,
                ShortHash = "abc1234", Message = "Fix posting", Author = "Ada", Ordering = 0,
            });
            ctx.OeProjectBuildLogs.Add(new ProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "core", Content = "Cloned.", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var detail = await Svc(read).GetBuildDetailAsync(buildId);

        detail.Should().NotBeNull();
        detail!.Artifacts.Should().ContainSingle();
        detail.RepoCommits.Should().ContainSingle().Which.CommitHash.Should().Be("abc1234");
        detail.Changelog.Should().ContainSingle().Which.RepoName.Should().Be("core");
        detail.Changelog[0].Commits.Should().ContainSingle().Which.Message.Should().Be("Fix posting");
        detail.Logs.Should().ContainSingle();
    }

    [Fact]
    public async Task ListBuildsAsync_surfaces_each_build_head_commit_and_count()
    {
        int projectId, withCommits, summaryNote;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S", repoNames: new[] { "core" });
            var repoId = ctx.OeProjectRepositories.First(r => r.ProjectId == projectId).Id;

            // A build with two changelog commits — the head (Ordering 0) represents the row.
            withCommits = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));
            ctx.OeProjectBuildCommits.AddRange(
                new ProjectBuildCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = withCommits, ProjectRepositoryId = repoId, ShortHash = "head123", Message = "Add posting-date validation", Author = "Ada", Ordering = 0 },
                new ProjectBuildCommit { OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = withCommits, ProjectRepositoryId = repoId, ShortHash = "old456", Message = "Earlier change", Author = "Ada", Ordering = 1 });

            // An earlier build recorded as a build-level summary note (empty hash, message only).
            summaryNote = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            ctx.OeProjectBuildCommits.Add(new ProjectBuildCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = summaryNote, ShortHash = "", Message = "First build", Author = "", Ordering = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var builds = await Svc(read).ListBuildsAsync(projectId);

        var head = builds.Single(b => b.Id == withCommits);
        head.HeadCommitShort.Should().Be("head123", "the head commit (Ordering 0) represents the build");
        head.HeadCommitMessage.Should().Be("Add posting-date validation");
        head.CommitCount.Should().Be(2, "so the row can hint at the remaining commits");

        var note = builds.Single(b => b.Id == summaryNote);
        note.HeadCommitShort.Should().BeNull("a summary note has no commit hash");
        note.HeadCommitMessage.Should().Be("First build", "but its message still describes the build");
    }

    [Fact]
    public async Task ListComparableBuildsAsync_only_returns_ready_builds_with_a_release()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), releaseId: await SeedReleaseAsync(ctx));
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc), releaseId: await SeedReleaseAsync(ctx));
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Failed, new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc));
            await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc)); // ready but no release
        }

        await using var read = _db.NewContext();
        var comparable = await Svc(read).ListComparableBuildsAsync(projectId);

        comparable.Should().HaveCount(2, "only ready builds that produced a navigable release can be compared");
        comparable.Should().BeInDescendingOrder(c => c.StartedAt);
    }

    [Fact]
    public async Task Download_fetches_return_bytes_and_a_concatenated_log()
    {
        int buildId, artifactId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(ctx, "CRONUS A/S");
            buildId = await SeedBuildAsync(ctx, projectId, ProjectBuildStatus.Ready, DateTime.UtcNow);
            var art = new ProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                FileName = "CRONUS_Core_1.0.0.0.app", AppName = "Core", AppVersion = "1.0.0.0",
                SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = DateTime.UtcNow,
            };
            ctx.OeProjectBuildArtifacts.Add(art);
            ctx.OeProjectBuildLogs.Add(new ProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "Build", Content = "alc ok", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            artifactId = art.Id;
        }

        await using var read = _db.NewContext();
        var svc = Svc(read);

        (await svc.GetArtifactBytesAsync(buildId, artifactId))!.Content.Should().Equal(1, 2, 3);
        (await svc.GetAllArtifactBytesAsync(buildId)).Should().ContainSingle();
        var raw = await svc.GetRawLogAsync(buildId);
        raw.Should().NotBeNull();
        raw!.Content.Should().Contain("alc ok");
    }

    [Fact]
    public async Task Reads_are_scoped_to_the_acting_org()
    {
        int otherBuildId;
        await using (var ctx = _db.NewContext())
        {
            var otherProject = await SeedProjectAsync(ctx, "Other Co", orgId: TestDb.OtherOrgId);
            otherBuildId = await SeedBuildAsync(ctx, otherProject, ProjectBuildStatus.Ready, DateTime.UtcNow, orgId: TestDb.OtherOrgId);
        }

        await using var read = _db.NewContext(); // scoped to DefaultOrg
        (await Svc(read).ListProjectsAsync()).Should().BeEmpty("the other org's project is filtered out");
        (await Svc(read).GetBuildDetailAsync(otherBuildId)).Should().BeNull("the other org's build is filtered out");
    }

    // ── seeding helpers ─────────────────────────────────────────────────

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx, string name, string[]? repoNames = null, int orgId = TestDb.DefaultOrgId)
    {
        var project = new Project
        {
            OrganizationId = orgId, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Repositories = (repoNames ?? new[] { "repo" }).Select(n => new ProjectRepository
            {
                OrganizationId = orgId, Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
                Url = $"https://github.com/x/{n}", DisplayName = n,
            }).ToList(),
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<int> SeedReleaseAsync(Data.AppDbContext ctx, int orgId = TestDb.DefaultOrgId)
    {
        var release = new Release
        {
            OrganizationId = orgId, Label = "build", Kind = "project", Status = "ready",
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task<int> SeedBuildAsync(
        Data.AppDbContext ctx, int projectId, string status, DateTime startedAt,
        string? bcVersion = null, int artifactCount = 0, int? releaseId = null, string? branch = null, int orgId = TestDb.DefaultOrgId)
    {
        var build = new ProjectBuild
        {
            OrganizationId = orgId, ProjectId = projectId, Status = status, BcVersion = bcVersion, Branch = branch,
            StartedAt = startedAt, FinishedAt = status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed ? startedAt : null,
            ReleaseId = releaseId,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < artifactCount; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new ProjectBuildArtifact
            {
                OrganizationId = orgId, ProjectBuildId = build.Id,
                FileName = $"app{i}.app", AppName = $"App {i}", AppVersion = "1.0.0.0",
                SizeBytes = 1, Content = new byte[] { (byte)i }, CreatedAt = DateTime.UtcNow,
            });
        }
        if (artifactCount > 0) await ctx.SaveChangesAsync();
        return build.Id;
    }
}
