using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Access tab on the project detail page — the switch that turns project
/// visibility on (<c>.design/teams-and-visibility.md</c>, slice 3). Its named
/// user is a BC consultant who owns the CRONUS engagement project and wants it
/// restricted to the NDA team.
///
/// <para>Three things are pinned: the tab exists only for someone who can manage
/// the project, a save round-trips through <c>SetAccessAsync</c>, and choosing
/// Public clears the team picks rather than leaving a disabled control holding
/// values that the service would then refuse.</para>
/// </summary>
public sealed class ProjectDetailAccessTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestContext _ctx = new();

    private const int OwnerUserId = 9600;
    private const int OutsiderUserId = 9601;

    public ProjectDetailAccessTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<TeamService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(OutsiderUserId, "nils@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _db.Dispose();
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<(int ProjectId, int TeamId)> SeedAsync(bool withTeam = true)
    {
        await using var ctx = _db.NewContext();
        var project = new Project
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        if (!withTeam)
        {
            await ctx.SaveChangesAsync();
            return (project.Id, 0);
        }

        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "NDA team",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        return (project.Id, team.Id);
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_project_gets_no_access_tab()
    {
        var (projectId, _) = await SeedAsync();
        _db.OrgContext.CurrentUserId = OutsiderUserId;

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));

        cut.WaitForAssertion(() =>
            cut.FindAll(".settings__tabs button, .settings__tabs a")
                .Select(t => t.TextContent.Trim())
                .Should().NotContain("Access"));
    }

    [Fact]
    public async Task The_owner_gets_the_access_tab_with_the_three_choices()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        var labels = cut.FindAll(".module-card__title").Select(t => t.TextContent.Trim()).ToList();
        labels.Should().Contain(new[] { "Public", "Read-only for others", "Private" });
        // Only ever the one outline save on this tab - Generate stays the app's primary.
        cut.FindAll(".settings__body .btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_private_with_a_team_round_trips_through_the_service()
    {
        var (projectId, teamId) = await SeedAsync();

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        // Pick Private, then tick the NDA team, then save.
        await PickAsync(cut, "Private");
        cut.WaitForState(() => cut.FindAll("input[type=checkbox]").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll("input[type=checkbox]")[0].Change(true));
        await ClickSaveAccessAsync(cut);

        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
            .Visibility.Should().Be(ProjectVisibility.Private);
        (await verify.OeProjectTeams.AsNoTracking().Where(t => t.ProjectId == projectId).Select(t => t.TeamId)
            .ToListAsync()).Should().Equal(teamId);
    }

    [Fact]
    public async Task Choosing_public_clears_the_team_picks_so_the_save_is_not_refused()
    {
        var (projectId, teamId) = await SeedAsync();
        // Start Private-with-team, the state the user is undoing.
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        await PickAsync(cut, "Public");
        await ClickSaveAccessAsync(cut);

        cut.FindAll(".field-error").Should().BeEmpty("clearing the picks is what keeps this save legal");
        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
            .Visibility.Should().Be(ProjectVisibility.Public);
        (await verify.OeProjectTeams.AsNoTracking().AnyAsync(t => t.ProjectId == projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task Private_with_no_team_ticked_shows_the_services_error_next_to_the_teams()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");
        await ClickSaveAccessAsync(cut);

        cut.Find(".field-error").TextContent.Should().Contain("at least one team");
    }

    [Fact]
    public async Task With_no_teams_in_the_org_a_non_admin_is_told_to_ask_one()
    {
        var (projectId, _) = await SeedAsync(withTeam: false);

        var cut = _ctx.RenderComponent<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No teams yet");
        cut.Find(".empty-state__text").TextContent.Should().Contain("Ask an admin");
        cut.FindAll(".empty-state__action").Should().BeEmpty("only an admin gets the create button");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task OpenAccessTabAsync(IRenderedComponent<ProjectDetail> cut)
    {
        cut.WaitForState(() => cut.FindAll(".settings__tabs button")
            .Any(t => t.TextContent.Trim() == "Access"));
        var tab = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == "Access");
        await cut.InvokeAsync(() => tab.Click());
    }

    private static async Task PickAsync(IRenderedComponent<ProjectDetail> cut, string label)
    {
        var card = cut.FindAll("label.module-card")
            .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == label);
        await cut.InvokeAsync(() => card.QuerySelector("input[type=radio]")!.Change(true));
    }

    private static async Task ClickSaveAccessAsync(IRenderedComponent<ProjectDetail> cut)
    {
        var save = cut.FindAll(".settings__body button")
            .First(b => b.TextContent.Contains("Save access"));
        await cut.InvokeAsync(() => save.Click());
    }
}
