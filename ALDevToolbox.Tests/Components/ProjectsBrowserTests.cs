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

    private readonly Microsoft.AspNetCore.Http.HttpContextAccessor _http = new();

    /// <summary>The page is a plain GET page and reads its address from the request.</summary>
    private void RequestWith(string query) => _http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
    {
        Request = { QueryString = new Microsoft.AspNetCore.Http.QueryString(query) },
    };

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
        _ctx.Services.AddScoped<CustomerModuleService>();
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(_http);
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
            cut.Find(".empty-state__action a").GetAttribute("href").Should().Be("/solutions/new");
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
        db.OeProjectTeams.Add(new OeProjectTeam
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
        var project = new OeProject
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
            // A pipeline's build: only those carry the row's status. A build
            // with no pipeline (a pull-request check) is deliberately not one.
            var pipeline = new OePipeline
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Name = "Default",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.OePipelines.Add(pipeline);
            await db.SaveChangesAsync();
            db.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                PipelineId = pipeline.Id,
                Status = status,
                BcVersion = bcVersion,
                StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    // ── The customer summary beside the list (.design/solution-customer-info.md) ──

    private async Task<int> DescribeAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Name == name);
        project.HostingType = ProjectHostingType.CustomerHardware;
        project.BcVersion = "NAV 2018 CU12";
        project.AccessDescription = "VPN, then remote desktop to CRONUS-BC01.";
        ctx.OeProjectContacts.Add(new OeProjectContact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Type = ProjectContactType.Customer,
            Name = "Annette Hill", Phone = "+45 12 34 56 78", CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task The_list_says_where_each_customer_runs_and_keeps_the_rest_for_the_summary()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await DescribeAsync("CRONUS Norway");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("thead th").Select(h => h.TextContent.Trim()).Should().Equal(
                "Latest build", "Solution", "Hosted by", "BC version", "Latest build", "Owner", "Actions");
            var cells = cut.Find("tbody tr").Children;
            cells[2].TextContent.Should().Be("Customer's hardware");
            cells[3].TextContent.Should().Be("NAV 2018 CU12");
            cells[4].TextContent.Trim().Should().Be("Built for BC 26.0", "two bare versions side by side get read out wrong on a call");
            cut.FindAll(".detail-body__aside").Should().BeEmpty("no column is reserved until a customer is chosen");
            cut.Markup.Should().NotContain("Annette Hill");
        });
    }

    [Fact]
    public async Task Choosing_summary_opens_that_customers_essentials_beside_the_list_as_an_address()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await DescribeAsync("CRONUS Norway");
        RequestWith($"?q=cronus&selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").ClassList.Should().Contain("is-selected");
            var rail = cut.Find(".detail-body__aside");
            rail.TextContent.Should().Contain("CRONUS Norway").And.Contain("VPN, then remote desktop")
                .And.Contain("Annette Hill").And.Contain("The customer, on their own hardware");
            rail.QuerySelector("a[href^='tel:']")!.GetAttribute("href").Should().Be("tel:+4512345678");
            rail.QuerySelector("a[aria-label='Close customer info']")!.GetAttribute("href").Should().Be("/solutions?q=cronus",
                "closing keeps the search the person had");
            cut.Find($"a[aria-label='Customer info for CRONUS Norway']").GetAttribute("href").Should().Be($"/solutions?q=cronus&selected={id}");
            rail.QuerySelectorAll("input, select, textarea").Should().BeEmpty("the summary never edits; the Customer tab does");
        });
    }

    [Fact]
    public async Task A_private_solution_the_viewer_is_not_on_cannot_be_summarised_by_putting_its_id_in_the_address()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await DescribeAsync("CRONUS Norway");
        await MakePrivateAsync("CRONUS Norway");
        RequestWith($"?selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr.projects__locked").Should().NotBeNull();
            cut.FindAll(".detail-body__aside").Should().BeEmpty();
            cut.Markup.Should().NotContain("Annette Hill").And.NotContain("NAV 2018");
        });
    }
}
