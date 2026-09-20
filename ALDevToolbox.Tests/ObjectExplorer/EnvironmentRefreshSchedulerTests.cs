using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task A_solution_with_only_a_tenant_is_swept_once_the_organisation_has_a_registration()
    {
        var tenantOnly = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: null, secret: null);
        var halfOwn = await SeedProjectAsync("Own client id, no secret", tenant: Guid.NewGuid(), clientId: "client-abc", secret: null);

        await using (var before = _db.NewContext())
        {
            (await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(before, default)).Should().BeEmpty();
        }

        await using (var seed = _db.NewContext())
        {
            var settings = await seed.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                seed.OrganizationSettings.Add(settings);
            }
            settings.BcClientId = "11111111-1111-1111-1111-111111111111";
            settings.BcClientSecretEncrypted = "cipher";
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var targets = await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(ctx, default);

        targets.Should().ContainSingle().Which.Should().Be(tenantOnly,
            "a solution that chose its own registration never falls back to the organisation's");
        targets.Should().NotContain(halfOwn);
    }

    [Fact]
    public void The_sweep_starts_a_few_minutes_into_its_hour_once_a_night()
    {
        var offset = TimeSpan.FromMinutes(17);
        DateTime At(int hour, int minute) => new(2026, 9, 21, hour, minute, 0, DateTimeKind.Utc);

        EnvironmentRefreshScheduler.IsDue(At(3, 15), null, offset).Should().BeFalse("it is not on the hour with everybody else");
        EnvironmentRefreshScheduler.IsDue(At(3, 20), null, offset).Should().BeTrue("the first five-minute poll past the offset");
        EnvironmentRefreshScheduler.IsDue(At(3, 25), new DateOnly(2026, 9, 21), offset).Should().BeFalse("once a night");
        EnvironmentRefreshScheduler.IsDue(At(3, 20), new DateOnly(2026, 9, 20), offset).Should().BeTrue("yesterday's sweep does not count");
        EnvironmentRefreshScheduler.IsDue(At(4, 0), null, offset).Should().BeFalse();
        (TimeSpan.FromMinutes(55) > EnvironmentRefreshScheduler.MaxStartOffset).Should().BeTrue(
            "the latest start plus one five-minute poll still has to land inside the sweep hour");
    }

    private async Task<int> SeedProjectAsync(
        string name, Guid? tenant, string? clientId, string? secret, DateTime? deletedAt = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
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
