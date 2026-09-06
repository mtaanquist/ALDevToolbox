using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the Projects directory against the design system's list archetype,
/// table view. The rule under test is the handoff's one rule for tables: a
/// row's status is the 4px edge keyline plus a leading glyph — never a pill in
/// a cell — and a project with no build yet gets neither, because an absent
/// build is not a status.
/// </summary>
public sealed class ProjectsBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public ProjectsBrowserTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_org_with_no_projects_gets_the_first_run_empty_state_and_its_create_action()
    {
        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card .empty-state__title").TextContent.Trim().Should().Be("No solutions yet");
            cut.Find(".empty-state__action").GetAttribute("href").Should().Be("/solutions/new");
        });
    }

    [Fact]
    public async Task A_projects_build_status_is_the_row_edge_and_a_glyph_never_a_pill()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Failed, bcVersion: null);

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("table.data-table--edge").Should().NotBeNull();
            cut.Find("tr.is-failed td.data-table__col-state .data-table__state--icon")
                .GetAttribute("aria-label").Should().Be("Failed");
            cut.FindAll("table .status-pill").Should().BeEmpty(
                "a table row carries status as the edge bar and glyph, never a pill");
            cut.FindAll("table .build-pill").Should().BeEmpty(
                "BuildStatusPill is the pre-redesign treatment and has no place in a row");
        });
    }

    [Fact]
    public async Task A_project_with_no_build_gets_no_edge_class_and_no_glyph()
    {
        await SeedProjectAsync("CRONUS Sweden", status: null, bcVersion: null);

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No builds yet");
            cut.FindAll("tbody tr[class*='is-']").Should().BeEmpty(
                "an absent build is not a status — colouring the edge would invent one");
            cut.FindAll(".data-table__state--icon").Should().BeEmpty();
        });
    }

    /// <summary>
    /// The one row this page renders for something the viewer may not open: a
    /// private project they are not on the team for. It has to say the project
    /// exists (so it doesn't seem to vanish) and nothing else about it, and it must
    /// not look clickable. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    [Fact]
    public async Task A_private_project_the_viewer_is_not_on_renders_as_a_locked_unclickable_row()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await MakePrivateAsync("CRONUS Denmark");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("tbody tr.projects__locked");
            row.TextContent.Should().Contain("CRONUS Denmark");
            row.TextContent.Should().Contain("Private — visible to its team");
            row.QuerySelectorAll("a").Should().BeEmpty("there is nothing behind the name for this viewer");
            cut.Markup.Should().NotContain("26.0", "a build version reports activity on the customer");
        });
    }

    /// <summary>
    /// Marks a seeded project private with a team nobody is on — the state a viewer
    /// with no grant sees. Written straight to the database because the UI that sets
    /// visibility lands in a later slice.
    /// </summary>
    private async Task MakePrivateAsync(string name)
    {
        await using var db = _db.NewContext();
        var project = await db.OeProjects.FirstAsync(p => p.Name == name);
        var team = new ALDevToolbox.Domain.Entities.Team
        {
            OrganizationId = project.OrganizationId,
            Name = "Nordics",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        project.Visibility = ProjectVisibility.Private;
        db.OeProjectTeams.Add(new ProjectTeam
        {
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            TeamId = team.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedProjectAsync(string name, string? status, string? bcVersion)
    {
        await using var db = _db.NewContext();
        var project = new Project
        {
            OrganizationId = _db.OrgContext.CurrentOrganizationId!.Value,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.OeProjects.Add(project);
        await db.SaveChangesAsync();

        if (status is not null)
        {
            db.OeProjectBuilds.Add(new ProjectBuild
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Status = status,
                BcVersion = bcVersion,
                StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }
}
