using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Target selection for the nightly environment-refresh sweep
/// (<see cref="EnvironmentRefreshScheduler.ResolveProjectIdsAsync"/>): only projects with
/// a complete Business Central connection are offered, and a deleted project is never
/// swept. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class EnvironmentRefreshSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_live_projects_with_complete_credentials_are_swept()
    {
        var connected = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        await SeedProjectAsync("No secret", tenant: Guid.NewGuid(), clientId: "client-abc", secret: null);
        await SeedProjectAsync("No client id", tenant: Guid.NewGuid(), clientId: null, secret: "cipher");
        await SeedProjectAsync("Nothing configured", tenant: null, clientId: null, secret: null);
        await SeedProjectAsync("Deleted", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher",
            deletedAt: DateTime.UtcNow);

        await using var ctx = _db.NewContext();
        var targets = await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(ctx, default);

        targets.Should().ContainSingle().Which.Should().Be(connected,
            "a project missing a credential part could only produce a nightly token failure, "
            + "and a deleted project has no customer to sweep");
    }

    private async Task<int> SeedProjectAsync(
        string name, Guid? tenant, string? clientId, string? secret, DateTime? deletedAt = null)
    {
        await using var ctx = _db.NewContext();
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
            BcTenantId = tenant,
            BcClientId = clientId,
            BcClientSecretEncrypted = secret,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }
}
