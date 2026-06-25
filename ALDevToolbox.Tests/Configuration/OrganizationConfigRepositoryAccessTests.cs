using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Round-trip for the per-org repository-access PATs on
/// <see cref="OrganizationConfigService"/>: each token is encrypted, the view
/// reports presence without exposing it, an empty token keeps the stored one,
/// clearing removes it, resolution returns the right plaintext per provider, and
/// the audit interceptor redacts both ciphertext columns. Mirrors the
/// machine-translation key contract.
/// </summary>
public sealed class OrganizationConfigRepositoryAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Save_encrypts_pats_and_view_reports_stored_without_exposing_them()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput(
            AzureDevOpsPat: "azure-secret-token", ClearAzureDevOpsPat: false,
            GitHubPat: "github-secret-token", ClearGitHubPat: false));

        var view = await svc.GetRepositoryAccessViewAsync();
        view.HasAzureDevOpsPat.Should().BeTrue();
        view.HasGitHubPat.Should().BeTrue();

        await using var verify = _db.NewContext();
        var stored = await verify.OrganizationSettings
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => new { s.AzureDevOpsPatEncrypted, s.GitHubPatEncrypted })
            .FirstAsync();
        stored.AzureDevOpsPatEncrypted.Should().NotBeNullOrEmpty();
        stored.GitHubPatEncrypted.Should().NotBeNullOrEmpty();
        stored.AzureDevOpsPatEncrypted.Should().NotContain("azure-secret-token", "the column stores ciphertext");
        stored.GitHubPatEncrypted.Should().NotContain("github-secret-token", "the column stores ciphertext");
    }

    [Fact]
    public async Task Resolve_returns_pat_per_provider_and_null_by_default()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.AzureDevOps)).Should().BeNull("nothing is configured by default");
        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.GitHub)).Should().BeNull();

        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput("az-pat", false, "gh-pat", false));

        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.AzureDevOps)).Should().Be("az-pat");
        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.GitHub)).Should().Be("gh-pat");
    }

    [Fact]
    public async Task Empty_token_keeps_the_stored_one()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput("az-pat", false, "gh-pat", false));

        // The form posts blank to keep the stored token; a blank value must not wipe it.
        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput(null, false, null, false));

        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.AzureDevOps)).Should().Be("az-pat");
        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.GitHub)).Should().Be("gh-pat");
    }

    [Fact]
    public async Task Clear_token_removes_only_the_targeted_provider()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput("az-pat", false, "gh-pat", false));

        // Clear only Azure DevOps; GitHub stays.
        await svc.SaveRepositoryAccessAsync(new RepositoryAccessInput(null, true, null, false));

        var view = await svc.GetRepositoryAccessViewAsync();
        view.HasAzureDevOpsPat.Should().BeFalse();
        view.HasGitHubPat.Should().BeTrue();
        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.AzureDevOps)).Should().BeNull();
        (await svc.ResolveRepositoryPatAsync(RepositoryProvider.GitHub)).Should().Be("gh-pat");
    }

    [Fact]
    public async Task Save_writes_audit_rows_with_redacted_pats()
    {
        // First save creates the row with both tokens.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await _db.NewOrganizationConfigService(ctx)
                .SaveRepositoryAccessAsync(new RepositoryAccessInput("first-azure-secret", false, "first-github-secret", false));
        }
        // Second save: the Updated audit row's snapshot captures the previous
        // row state, which holds ciphertext for the first tokens.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await _db.NewOrganizationConfigService(ctx)
                .SaveRepositoryAccessAsync(new RepositoryAccessInput("second-azure-secret", false, "second-github-secret", false));
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.OrganizationSettings)
            .ToListAsync();
        rows.Should().NotBeEmpty();
        foreach (var row in rows.Where(r => r.SnapshotJson is not null))
        {
            row.SnapshotJson.Should().NotContain("first-azure-secret");
            row.SnapshotJson.Should().NotContain("first-github-secret");
            row.SnapshotJson.Should().NotContain("second-azure-secret");
            row.SnapshotJson.Should().NotContain("second-github-secret");
        }
        rows.Where(r => r.SnapshotJson is not null && r.SnapshotJson.Contains("[redacted]"))
            .Should().NotBeEmpty("the PAT columns are replaced with a fixed sentinel before snapshotting");
    }
}
