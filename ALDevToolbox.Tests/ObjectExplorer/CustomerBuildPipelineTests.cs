using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Integration coverage for the customer-build pipeline's durable + report
/// surfaces — the parts reachable without a real <c>git</c>/<c>alc</c>/CDN:
/// the external-process seam contract (<see cref="ProcessRunner"/>), durable
/// resume of a <c>customer_build</c> job across a restart
/// (<see cref="PersistedImportJobs"/>), and the per-app build report the manage
/// page renders (<see cref="ObjectExplorerService.GetCustomerBuildResultsAsync"/>),
/// including the partial-failure shape (one extension ingested, one failed).
///
/// <para>
/// The clone → resolve-symbols → compile → ingest path itself drives concrete,
/// network-bound services (<see cref="BcArtifactService"/> against Microsoft's
/// CDN, <see cref="AlCompilerProvisioner"/> against NuGet) that aren't seamed
/// behind interfaces, so the full end-to-end is exercised by the staging smoke,
/// not here. The pure build logic is covered by <see cref="CustomerBuildServiceTests"/>.
/// </para>
/// </summary>
public sealed class CustomerBuildPipelineTests : IDisposable
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

    // ── Durable resume of a customer_build job ──────────────────────────

    [Fact]
    public async Task CustomerBuild_job_persists_kind_and_customer_id()
    {
        var releaseId = await SeedCustomerReleaseAsync(status: "ingesting");

        await using var ctx = _db.NewContext();
        var jobs = new PersistedImportJobs(ctx, TimeProvider.System);

        var rowId = await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.CustomerBuild(77), storeSymbolReference: false);

        await using var verify = _db.NewContext();
        var row = await verify.OeImportJobs.FindAsync(rowId);
        row!.Kind.Should().Be("customer_build");
        row.CustomerId.Should().Be(77);
        row.Status.Should().Be("queued");
    }

    [Fact]
    public async Task ReconcileOnStartup_reenqueues_customer_build_as_resumable_job()
    {
        var releaseId = await SeedCustomerReleaseAsync(status: "ingesting");
        await using (var ctx = _db.NewContext())
        {
            var jobs = new PersistedImportJobs(ctx, TimeProvider.System);
            await jobs.CreateAsync(releaseId, Identity(), new ReleaseImportSource.CustomerBuild(77), storeSymbolReference: false);
        }

        // A fresh context = a fresh process: the reconciler picks up the survivor.
        await using var reconcileCtx = _db.NewContext();
        var reconciler = new PersistedImportJobs(reconcileCtx, TimeProvider.System);
        var resumed = await reconciler.ReconcileOnStartupAsync();

        resumed.Should().ContainSingle();
        var job = resumed[0];
        job.ReleaseId.Should().Be(releaseId);
        job.Source.Should().BeOfType<ReleaseImportSource.CustomerBuild>()
            .Which.CustomerId.Should().Be(77);
    }

    // ── Build report (manage page surface) ──────────────────────────────

    [Fact]
    public async Task Build_report_lists_failures_first_and_reflects_a_partial_build()
    {
        // The Core+ContiniaExts shape: one extension ingested, one failed —
        // a partial build (release ready, one failed row).
        var releaseId = await SeedCustomerReleaseAsync(status: "ready");
        await using (var seed = _db.NewContext())
        {
            seed.OeCustomerBuildResults.AddRange(
                Result(releaseId, "Core", CustomerBuildResultStatus.Ingested, null),
                Result(releaseId, "ContiniaExts", CustomerBuildResultStatus.Failed, "Missing dependency symbols."));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, NullLogger<ReferenceQueryService>.Instance),
            NullLogger<ObjectExplorerService>.Instance);

        var report = await svc.GetCustomerBuildResultsAsync(releaseId);

        report.Should().HaveCount(2);
        report[0].Status.Should().Be(CustomerBuildResultStatus.Failed, "failures sort first so the admin sees what to fix");
        report[0].AppName.Should().Be("ContiniaExts");
        report[0].Message.Should().Be("Missing dependency symbols.");
        report.Should().Contain(r => r.AppName == "Core" && r.Status == CustomerBuildResultStatus.Ingested);
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
                Kind = "customer",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(release);
            await seed.SaveChangesAsync();
            otherReleaseId = release.Id;
            seed.OeCustomerBuildResults.Add(Result(otherReleaseId, "Hidden", CustomerBuildResultStatus.Failed, "secret", TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new ObjectExplorerService(ctx, new ReferenceQueryService(ctx, NullLogger<ReferenceQueryService>.Instance),
            NullLogger<ObjectExplorerService>.Instance);

        (await svc.GetCustomerBuildResultsAsync(otherReleaseId)).Should().BeEmpty("the query filter scopes to the acting org");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static AmbientOrganizationScope.OrganizationIdentity Identity() =>
        new(TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private async Task<int> SeedCustomerReleaseAsync(string status)
    {
        await using var ctx = _db.NewContext();
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Acme (building…) " + Guid.NewGuid().ToString("N"),
            Kind = "customer",
            Status = status,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static CustomerBuildResult Result(int releaseId, string app, string status, string? message, int orgId = TestDb.DefaultOrgId) =>
        new()
        {
            OrganizationId = orgId,
            ReleaseId = releaseId,
            AppName = app,
            AppId = Guid.NewGuid().ToString(),
            Status = status,
            Message = message,
            CreatedAt = DateTime.UtcNow,
        };
}
