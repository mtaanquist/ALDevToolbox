using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The request-side contract of <see cref="ProjectDiscoveryService"/>:
/// <see cref="ProjectDiscoveryService.RequestDiscoveryAsync"/> is access-gated and
/// enqueues, and <see cref="ProjectDiscoveryService.GetDiscoveryAsync"/> reads the
/// cached checklist back with its <c>DiscoveredAt</c> / <c>Error</c> / <c>InFlight</c>
/// flags. The clone-and-walk itself runs in the worker and isn't exercised here.
/// </summary>
public sealed class ProjectDiscoveryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9400;

    public ProjectDiscoveryServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private ProjectDiscoveryService Svc(AppDbContext ctx, ProjectDiscoveryQueue queue) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), queue, NullLogger<ProjectDiscoveryService>.Instance);

    private async Task<int> SeedProjectAsync(int? ownerId = OwnerUserId, string? discoveredJson = null, DateTime? discoveredAt = null, string? error = null)
    {
        await using var ctx = _db.NewContext();
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S " + Guid.NewGuid().ToString("N"),
            CreatedByUserId = ownerId,
            DiscoveredExtensionsJson = discoveredJson,
            DiscoveredAt = discoveredAt,
            DiscoveryError = error,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task RequestDiscovery_enqueues_for_the_owner()
    {
        var id = await SeedProjectAsync();
        var queue = new ProjectDiscoveryQueue();
        await using var ctx = _db.NewContext();

        await Svc(ctx, queue).RequestDiscoveryAsync(id);

        queue.IsInFlight(id).Should().BeTrue();
    }

    [Fact]
    public async Task RequestDiscovery_is_blocked_for_a_non_owner_non_admin()
    {
        var id = await SeedProjectAsync(); // owned by OwnerUserId
        const int strangerId = 9500;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = strangerId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "stranger@example.com",
                PasswordHash = "x",
                DisplayName = "Stranger",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = strangerId;
        var queue = new ProjectDiscoveryQueue();
        try
        {
            await using var ctx = _db.NewContext();
            var act = () => Svc(ctx, queue).RequestDiscoveryAsync(id);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
            queue.IsInFlight(id).Should().BeFalse("a denied request never enqueues");
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    [Fact]
    public async Task RequestDiscovery_rejects_a_missing_project()
    {
        var queue = new ProjectDiscoveryQueue();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx, queue).RequestDiscoveryAsync(987654);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Discovery");
    }

    [Fact]
    public async Task GetDiscovery_parses_the_cached_list_and_surfaces_metadata()
    {
        var at = new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);
        var cached = JsonSerializer.Serialize(new List<DiscoveredExtension>
        {
            new("11111111-1111-1111-1111-111111111111", "Core", "CRONUS", "1.0.0.0", "https://github.com/cronus/core", "Core"),
            new("22222222-2222-2222-2222-222222222222", "Sales", "CRONUS", "2.0.0.0", "https://github.com/cronus/sales", "Sales"),
        });
        var id = await SeedProjectAsync(discoveredJson: cached, discoveredAt: at);
        var queue = new ProjectDiscoveryQueue();
        await using var ctx = _db.NewContext();

        var result = await Svc(ctx, queue).GetDiscoveryAsync(id);

        result.Extensions.Should().HaveCount(2);
        result.Extensions.Should().Contain(e => e.Name == "Core" && e.Version == "1.0.0.0");
        result.DiscoveredAt.Should().Be(at);
        result.Error.Should().BeNull();
        result.InFlight.Should().BeFalse();
    }

    [Fact]
    public async Task GetDiscovery_reports_in_flight_and_a_cached_error()
    {
        var id = await SeedProjectAsync(error: "No GitHub token for \"Core\".");
        var queue = new ProjectDiscoveryQueue();
        await queue.EnqueueAsync(new ProjectDiscoveryJob(id,
            new ALDevToolbox.Services.AmbientOrganizationScope.OrganizationIdentity(
                TestDb.DefaultOrgId, OwnerUserId, false, false)));
        await using var ctx = _db.NewContext();

        var result = await Svc(ctx, queue).GetDiscoveryAsync(id);

        result.Extensions.Should().BeEmpty();
        result.Error.Should().Be("No GitHub token for \"Core\".");
        result.InFlight.Should().BeTrue();
    }

    [Fact]
    public async Task GetDiscovery_on_a_missing_project_is_empty()
    {
        var queue = new ProjectDiscoveryQueue();
        await using var ctx = _db.NewContext();

        var result = await Svc(ctx, queue).GetDiscoveryAsync(987654);

        result.Extensions.Should().BeEmpty();
        result.DiscoveredAt.Should().BeNull();
        result.Error.Should().BeNull();
    }
}
