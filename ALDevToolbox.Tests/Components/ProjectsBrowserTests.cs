using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
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
    private readonly TestContext _ctx = new();

    public ProjectsBrowserTests()
    {
        var auth = _ctx.AddTestAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString));
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
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_org_with_no_projects_gets_the_first_run_empty_state_and_its_create_action()
    {
        var cut = _ctx.RenderComponent<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card .empty-state__title").TextContent.Trim().Should().Be("No projects yet");
            cut.Find(".empty-state__action").GetAttribute("href").Should().Be("/projects/new");
        });
    }

    [Fact]
    public async Task A_projects_build_status_is_the_row_edge_and_a_glyph_never_a_pill()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Failed, bcVersion: null);

        var cut = _ctx.RenderComponent<ProjectsBrowser>();

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

        var cut = _ctx.RenderComponent<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No builds yet");
            cut.FindAll("tbody tr[class*='is-']").Should().BeEmpty(
                "an absent build is not a status — colouring the edge would invent one");
            cut.FindAll(".data-table__state--icon").Should().BeEmpty();
        });
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
