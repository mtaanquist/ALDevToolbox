using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
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
    private readonly BunitContext _ctx = new();

    private const int OwnerUserId = 9600;
    private const int OutsiderUserId = 9601;

    public ProjectDetailAccessTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<TeamService>();
        // ProjectDetail loads the Business Central tab's connection for anyone who
        // can manage the project, so its chain has to resolve even though this test
        // never touches BC. The clients are never called.
        _ctx.Services.AddHttpClient();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _db.AddStorageServices(_ctx.Services);
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
        _db.WaitForQueriesToSettle();
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

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));

        cut.WaitForAssertion(() =>
            cut.FindAll(".settings__tabs button, .settings__tabs a")
                .Select(t => t.TextContent.Trim())
                .Should().NotContain("Access"));
    }

    [Fact]
    public async Task The_owner_gets_the_access_tab_with_the_three_choices()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
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

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
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

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
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

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");
        await ClickSaveAccessAsync(cut);

        cut.Find(".field-error").TextContent.Should().Contain("at least one team");
    }

    [Fact]
    public async Task With_no_teams_in_the_org_a_non_admin_is_pointed_at_the_teams_page()
    {
        var (projectId, _) = await SeedAsync(withTeam: false);

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No teams yet");
        // A link to somewhere they can act, not a dead end: /teams is open to
        // every signed-in user.
        cut.Find(".empty-state__text a").GetAttribute("href").Should().Be("/teams");
        cut.FindAll(".empty-state__text a[href='/admin/administration/teams']")
            .Should().BeEmpty("only an admin gets the create path");
    }

    /// <summary>
    /// The blocker this review caught: with no teams in the org, the restricted
    /// levels used to stay selectable, and saving one produced a service error
    /// keyed "Teams" that had nowhere to render - a silent no-op. Both halves are
    /// pinned here: the levels are unreachable, and there is no save to press.
    /// </summary>
    [Fact]
    public async Task With_no_teams_the_restricted_levels_are_unavailable_and_there_is_nothing_to_save()
    {
        var (projectId, _) = await SeedAsync(withTeam: false);

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        foreach (var label in new[] { "Read-only for others", "Private" })
        {
            var card = cut.FindAll("label.module-card")
                .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == label);
            card.QuerySelector("input[type=radio]")!.HasAttribute("disabled")
                .Should().BeTrue($"{label} can't be saved without a team");
            card.TextContent.Should().Contain("Needs a team");
        }

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Save access"));
    }

    [Fact]
    public async Task Save_is_disabled_until_something_changes_and_discard_puts_it_back()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        SaveButton(cut).HasAttribute("disabled").Should().BeTrue("nothing has changed yet");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Discard changes"));

        await PickAsync(cut, "Private");
        SaveButton(cut).HasAttribute("disabled").Should().BeFalse();

        var discard = cut.FindAll("button").First(b => b.TextContent.Contains("Discard changes"));
        await cut.InvokeAsync(() => discard.Click());

        SaveButton(cut).HasAttribute("disabled").Should().BeTrue("discard restored the loaded state");
        cut.FindAll("label.module-card.is-selected")
            .Single().QuerySelector(".module-card__title")!.TextContent.Trim().Should().Be("Public");
    }

    [Fact]
    public async Task Public_collapses_the_team_list_to_one_line_and_a_restricted_level_brings_it_back()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        cut.FindAll("input[type=checkbox]").Should().BeEmpty("a public project has no teams to pick");
        cut.Markup.Should().Contain("Teams don't apply to a public project.");

        await PickAsync(cut, "Private");
        cut.FindAll("input[type=checkbox]").Should().ContainSingle("the list is the affordance");
        cut.Markup.Should().Contain("Teams that keep access");
    }

    [Fact]
    public async Task A_team_card_names_who_is_on_it()
    {
        var (projectId, teamId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = OwnerUserId,
                IsManager = true, CreatedAt = DateTime.UtcNow,
            });
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = OutsiderUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");

        var card = cut.FindAll("label.module-card")
            .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == "NDA team");
        // Managers first - the person a reader checks for runs the account.
        card.TextContent.Should().Contain("owner@example.com");
    }

    [Fact]
    public async Task The_projects_visibility_shows_beside_its_name_on_every_tab()
    {
        var (projectId, teamId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        cut.WaitForAssertion(() =>
            cut.Find(".detail-head__title-row .status-pill").TextContent.Trim().Should().Be("Private"));
    }

    [Fact]
    public async Task A_public_project_gets_no_badge()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.Id, projectId));
        await OpenAccessTabAsync(cut);

        cut.FindAll(".detail-head__title-row .status-pill").Should().BeEmpty();
    }

    private static IElement SaveButton(IRenderedComponent<ProjectDetail> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Save access"));

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

    /// <summary>
    /// Clicks "Save access" and waits for the page to say what happened. The
    /// handler is async, so the click returns before the write lands — without
    /// the wait every assertion below would race it.
    /// </summary>
    private static async Task ClickSaveAccessAsync(IRenderedComponent<ProjectDetail> cut)
    {
        var save = SaveButton(cut);
        await cut.InvokeAsync(() => save.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        cut.WaitForState(() => cut.FindAll(".alert--success").Any() || cut.FindAll(".field-error").Any());
    }
}
