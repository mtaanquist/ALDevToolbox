using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Integration coverage for the project-build pipeline's durable + report
/// surfaces — the parts reachable without a real <c>git</c>/<c>alc</c>/CDN:
/// the external-process seam contract (<see cref="ProcessRunner"/>), durable
/// resume of a <c>project_build</c> job across a restart
/// (<see cref="PersistedImportJobs"/>), and the per-app build report the manage
/// page renders (<see cref="ObjectExplorerService.GetProjectBuildResultsAsync"/>),
/// including the partial-failure shape (one extension ingested, one failed).
///
/// <para>
/// The clone → resolve-symbols → compile → ingest path itself drives concrete,
/// network-bound services (<see cref="BcArtifactService"/> against Microsoft's
/// CDN, <see cref="AlCompilerProvisioner"/> against NuGet) that aren't seamed
/// behind interfaces, so the full end-to-end is exercised by the staging smoke,
/// not here. The pure build logic is covered by <see cref="ProjectBuildServiceTests"/>.
/// </para>
/// </summary>
public sealed class ProjectBuildPipelineTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── External-process seam (ProcessRunner) ───────────────────────────

    [Fact]
    public async Task ProcessRunner_captures_stdout_and_success_on_exit_zero()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest("/bin/sh", new[] { "-c", "echo hello" }));

        result.Succeeded.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.StdOut.Trim().Should().Be("hello");
    }

    [Fact]
    public async Task ProcessRunner_captures_exit_code_and_stderr_on_failure()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest("/bin/sh", new[] { "-c", "echo oops 1>&2; exit 4" }));

        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().Be(4);
        result.StdErr.Trim().Should().Be("oops");
    }

    [Fact]
    public async Task ProcessRunner_honours_working_directory_and_env()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRunRequest(
            "/bin/sh", new[] { "-c", "echo $OE_TEST_VAR in $(pwd)" },
            WorkingDirectory: "/tmp",
            Environment: new Dictionary<string, string> { ["OE_TEST_VAR"] = "marker" }));

        result.StdOut.Should().Contain("marker in /tmp");
    }

    // ── Durable resume of a project_build job ──────────────────────────

    [Fact]
    public async Task ProjectBuild_job_persists_kind_and_project_id()
    {
        var releaseId = await SeedProjectReleaseAsync(status: "ingesting");

        await using var ctx = _db.NewContext();
        var jobs = new PersistedImportJobs(ctx, TimeProvider.System);

        var rowId = await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.ProjectBuild(77), storeSymbolReference: false);

        await using var verify = _db.NewContext();
        var row = await verify.OeImportJobs.FindAsync(rowId);
        row!.Kind.Should().Be("project_build");
        row.ProjectId.Should().Be(77);
        row.Status.Should().Be("queued");
    }

    [Fact]
    public async Task ReconcileOnStartup_reenqueues_project_build_as_resumable_job()
    {
        var releaseId = await SeedProjectReleaseAsync(status: "ingesting");
        await using (var ctx = _db.NewContext())
        {
            var jobs = new PersistedImportJobs(ctx, TimeProvider.System);
            await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.ProjectBuild(77), storeSymbolReference: false);
        }

        // A fresh context = a fresh process: the reconciler picks up the survivor.
        await using var reconcileCtx = _db.NewContext();
        var reconciler = new PersistedImportJobs(reconcileCtx, TimeProvider.System);
        var resumed = await reconciler.ReconcileOnStartupAsync();

        resumed.Should().ContainSingle();
        var job = resumed[0];
        job.ReleaseId.Should().Be(releaseId);
        job.Source.Should().BeOfType<ReleaseImportSource.ProjectBuild>()
            .Which.ProjectId.Should().Be(77);
    }

    // ── Build report (manage page surface) ──────────────────────────────

    [Fact]
    public async Task Build_report_lists_failures_first_and_reflects_a_partial_build()
    {
        // The Core+ContiniaExts shape: one extension ingested, one failed —
        // a partial build (release ready, one failed row).
        var commit = new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);
        var releaseId = await SeedProjectReleaseAsync(status: "ready");
        await using (var seed = _db.NewContext())
        {
            seed.OeProjectBuildResults.AddRange(
                Result(releaseId, "Core", ProjectBuildResultStatus.Ingested, null,
                    repoUrl: "https://github.com/acme/core", commitSha: "abc1234def5678", commitDate: commit),
                Result(releaseId, "ContiniaExts", ProjectBuildResultStatus.Failed, "Missing dependency symbols."));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, NullLogger<ReferenceQueryService>.Instance),
            NullLogger<ObjectExplorerService>.Instance);

        var report = await svc.GetProjectBuildResultsAsync(releaseId);

        report.Should().HaveCount(2);
        report[0].Status.Should().Be(ProjectBuildResultStatus.Failed, "failures sort first so the admin sees what to fix");
        report[0].AppName.Should().Be("ContiniaExts");
        report[0].Message.Should().Be("Missing dependency symbols.");

        var core = report.Single(r => r.AppName == "Core");
        core.Status.Should().Be(ProjectBuildResultStatus.Ingested);
        core.RepoUrl.Should().Be("https://github.com/acme/core", "build provenance round-trips for the future Artifacts surface");
        core.CommitSha.Should().Be("abc1234def5678");
        core.CommitDate.Should().Be(commit);
    }

    [Fact]
    public async Task Build_report_is_org_scoped()
    {
        // A build report on another org's release must not leak through the
        // query filter.
        int otherReleaseId;
        await using (var seed = _db.NewContext())
        {
            var release = new Release
            {
                OrganizationId = TestDb.OtherOrgId,
                Label = "Other on BC 26.0",
                Kind = "project",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(release);
            await seed.SaveChangesAsync();
            otherReleaseId = release.Id;
            seed.OeProjectBuildResults.Add(Result(otherReleaseId, "Hidden", ProjectBuildResultStatus.Failed, "secret", TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, NullLogger<ReferenceQueryService>.Instance),
            NullLogger<ObjectExplorerService>.Instance);

        (await svc.GetProjectBuildResultsAsync(otherReleaseId)).Should().BeEmpty("the query filter scopes to the acting org");
    }

    // ── Relaxed label uniqueness (#4) ───────────────────────────────────

    [Fact]
    public async Task Dedup_key_is_unique_per_org_but_labels_may_repeat()
    {
        // The label is display-only now — any kind may repeat it, because a row
        // with no dedup key never collides.
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleases.AddRange(
                Rel("Business Central 26.0 (DK)", "first_party"),
                Rel("Business Central 26.0 (DK)", "first_party"),
                Rel("Acme on BC 26.0", "project"),
                Rel("Acme on BC 26.0", "project"));
            var act = () => ctx.SaveChangesAsync();
            await act.Should().NotThrowAsync("keyless rows are disambiguated by the release id, not the label");
        }

        // Two active rows sharing a dedup key collide — the daily artifact sweep's
        // race backstop. The labels differ to prove it's the key, not the label.
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleases.AddRange(
                Rel("Business Central 28.0 (DK)", "first_party", "bc-onprem:28.0:dk"),
                Rel("BC 28.0 Denmark (renamed)", "first_party", "bc-onprem:28.0:dk"));
            var act = () => ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("the unique index guards the dedup key");
        }
    }

    private static Release Rel(string label, string kind, string? dedupKey = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        Kind = kind,
        DedupKey = dedupKey,
        Status = "ready",
        ImportedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── helpers ─────────────────────────────────────────────────────────

    private static AmbientOrganizationScope.OrganizationIdentity Identity() =>
        new(TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private async Task<int> SeedProjectReleaseAsync(string status)
    {
        await using var ctx = _db.NewContext();
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Acme (building…) " + Guid.NewGuid().ToString("N"),
            Kind = "project",
            Status = status,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static ProjectBuildResult Result(
        int releaseId, string app, string status, string? message, int orgId = TestDb.DefaultOrgId,
        string? repoUrl = null, string? commitSha = null, DateTime? commitDate = null) =>
        new()
        {
            OrganizationId = orgId,
            ReleaseId = releaseId,
            AppName = app,
            AppId = Guid.NewGuid().ToString(),
            Status = status,
            Message = message,
            RepoUrl = repoUrl,
            CommitSha = commitSha,
            CommitDate = commitDate,
            CreatedAt = DateTime.UtcNow,
        };
}
