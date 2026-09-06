using ALDevToolbox.Components.Pages.Environments;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The cross-solution Environments list (design archetype 2a). The named user is a
/// BC consultant checking every customer environment they can reach in one table.
///
/// <para>The rule most worth pinning is which timestamp "Last checked" reports.
/// A row carries two, stamped by different reads: the environment itself, and the
/// next-update mirror. A tenant can answer one and refuse the other, so reading the
/// wrong one reports an environment as never checked when only its updates were
/// unreadable.</para>
/// </summary>
public sealed class EnvironmentsListTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9800;

    public EnvironmentsListTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<UpgradeFleetService>();
        _ctx.Services.AddSingleton(new EnvironmentRefreshQueue());
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
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
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedEnvironmentAsync(
        int projectId, string name, string type, string? status,
        DateTime environmentFetchedAt, DateTime? updatesFetchedAt)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Status = status,
            Version = "28.2",
            FetchedAt = environmentFetchedAt,
            BcNextUpdateFetchedAt = updatesFetchedAt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task An_org_with_no_connected_solution_gets_the_first_run_empty_state()
    {
        await SeedSolutionAsync("CRONUS Denmark");

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() =>
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No environments to show yet"));
        // The empty state has to say how to get out of it, not just that it is empty.
        cut.Find(".empty-state__text").TextContent
            .Should().Contain("Business Central connection");
        cut.Find(".empty-state__action a").TextContent.Trim().Should().Be("Go to solutions");
    }

    [Fact]
    public async Task Last_checked_reports_the_environment_read_not_the_update_mirror()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        // Read half an hour ago; its updates have never been readable at all.
        await SeedEnvironmentAsync(id, "Production", "Production", "Active",
            environmentFetchedAt: DateTime.UtcNow.AddMinutes(-30),
            updatesFetchedAt: null);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(1));
        var lastChecked = cut.FindAll(".data-table tbody tr td").Last().TextContent.Trim();
        lastChecked.Should().NotBe("never",
            "the environment was read half an hour ago - only its updates were unreadable");
        lastChecked.Should().Contain("minutes ago");
    }

    [Fact]
    public async Task An_environment_part_way_through_an_update_does_not_need_attention()
    {
        var id = await SeedSolutionAsync("CRONUS Denmark");
        var now = DateTime.UtcNow;
        await SeedEnvironmentAsync(id, "Production", "Production", "Updating", now, now);
        await SeedEnvironmentAsync(id, "UAT", "Sandbox", "Suspended", now, now);
        await SeedEnvironmentAsync(id, "Test", "Sandbox", "Active", now, now);

        var cut = _ctx.Render<EnvironmentsList>();

        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(3));
        var tabs = cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).ToList();
        tabs.Should().Contain(t => t.StartsWith("Needs attention"));
        // Suspended counts; mid-update is the system working, so it must not.
        tabs.First(t => t.StartsWith("Needs attention")).Should().EndWith("1");
    }
}
