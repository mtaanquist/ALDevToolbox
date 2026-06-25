using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The auto-build change-detection rule
/// (<see cref="CustomerBuildService.HasRepoChangesSinceLastBuildAsync"/>) the nightly
/// <see cref="CustomerAutoBuildScheduler"/> gates each build on: a repo whose remote
/// HEAD (probed via a stubbed <c>git ls-remote</c>) differs from the commit recorded
/// in the last build — or one that's never been built — counts as changed; a matching
/// HEAD, or a repo with no PAT to probe with, does not.
/// </summary>
public sealed class CustomerAutoBuildTests : IDisposable
{
    private readonly TestDb _db = new();
    private const string RepoUrl = "https://github.com/acme/core";

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Reports_change_when_the_customer_has_never_been_built()
    {
        await using var ctx = _db.NewContext();
        var customerId = await SeedCustomerWithPatAsync(ctx);
        var svc = NewBuildService(ctx, new FakeProcessRunner { HeadSha = "aaaaaaaa" });

        (await svc.HasRepoChangesSinceLastBuildAsync(customerId)).Should().BeTrue();
    }

    [Fact]
    public async Task Reports_no_change_when_head_matches_the_last_build()
    {
        await using var ctx = _db.NewContext();
        var customerId = await SeedCustomerWithPatAsync(ctx);
        await SeedBuildAsync(ctx, customerId, builtSha: "aaaaaaaa");
        var svc = NewBuildService(ctx, new FakeProcessRunner { HeadSha = "aaaaaaaa" });

        (await svc.HasRepoChangesSinceLastBuildAsync(customerId)).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_change_when_head_moved_since_the_last_build()
    {
        await using var ctx = _db.NewContext();
        var customerId = await SeedCustomerWithPatAsync(ctx);
        await SeedBuildAsync(ctx, customerId, builtSha: "aaaaaaaa");
        var svc = NewBuildService(ctx, new FakeProcessRunner { HeadSha = "bbbbbbbb" });

        (await svc.HasRepoChangesSinceLastBuildAsync(customerId)).Should().BeTrue();
    }

    [Fact]
    public async Task Reports_no_change_when_no_pat_is_configured_to_probe_with()
    {
        await using var ctx = _db.NewContext();
        var customerId = await SeedCustomerWithPatAsync(ctx, withPat: false);
        // Even with a moved HEAD the probe can't run without a PAT, so the sweep
        // must not trigger a build that would only fail.
        var svc = NewBuildService(ctx, new FakeProcessRunner { HeadSha = "bbbbbbbb" });

        (await svc.HasRepoChangesSinceLastBuildAsync(customerId)).Should().BeFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<int> SeedCustomerWithPatAsync(Data.AppDbContext ctx, bool withPat = true)
    {
        if (withPat)
        {
            await _db.NewOrganizationConfigService(ctx)
                .SaveRepositoryAccessAsync(new RepositoryAccessInput(null, false, "ghp_test", false));
        }
        var customers = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);
        return await customers.CreateCustomerAsync(new CustomerInput(
            "Acme", "dk",
            new[] { new CustomerRepositoryInput(RepositoryProvider.GitHub, RepoUrl, "Core") },
            AutoBuildEnabled: true));
    }

    /// <summary>Seeds a prior customer build: a release, the import job that links it to the customer, and a build-result row recording the repo's built commit.</summary>
    private async Task SeedBuildAsync(Data.AppDbContext ctx, int customerId, string builtSha)
    {
        var release = new Release
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Acme on BC 26.0",
            Kind = "customer",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        ctx.OeImportJobs.Add(new ImportJob
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            CustomerId = customerId,
            Kind = "customer_build",
            Status = "completed",
            CreatedAt = DateTime.UtcNow,
        });
        ctx.OeCustomerBuildResults.Add(new CustomerBuildResult
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppName = "Core",
            AppId = "00000000-0000-0000-0000-000000000001",
            Status = CustomerBuildResultStatus.Ingested,
            RepoUrl = RepoUrl,
            CommitSha = builtSha,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private CustomerBuildService NewBuildService(Data.AppDbContext ctx, IProcessRunner runner)
    {
        var factory = new StubHttpClientFactory(new Dictionary<string, string>());
        var artifacts = new BcArtifactService(factory, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance);
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations, NullLogger<ReleaseImportService>.Instance);
        var compiler = new AlCompilerProvisioner(factory, NullLogger<AlCompilerProvisioner>.Instance);
        var orgConfig = _db.NewOrganizationConfigService(ctx);
        return new CustomerBuildService(
            ctx, _db.OrgContext, artifacts, importer, compiler, orgConfig, runner,
            TimeProvider.System, NullLogger<CustomerBuildService>.Instance);
    }

    /// <summary>A stub <see cref="IProcessRunner"/> standing in for <c>git ls-remote</c>: returns "&lt;sha&gt;\tHEAD" on success, or a non-zero exit when <see cref="Fail"/>.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string HeadSha { get; init; } = "aaaaaaaa";
        public bool Fail { get; init; }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(Fail
                ? new ProcessRunResult(128, "", "fatal: could not read from remote repository")
                : new ProcessRunResult(0, $"{HeadSha}\tHEAD\n", ""));
    }
}
