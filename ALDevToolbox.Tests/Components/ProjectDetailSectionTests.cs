using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The seams the tab split created on the solution settings page. Two of its
/// five tabs — General and Repositories — have no Save of their own: they edit
/// the model the page hands down and tell it something changed, and the page's
/// header primary is what writes. That handover is now a component boundary, so
/// it is pinned here rather than left to the page-level tests, which need a
/// database and so can't say anything about it cheaply.
///
/// <para>The other three tabs (Business Central, Pipelines, Access) are covered
/// through the page in <see cref="ProjectDetailAccessTests"/> and
/// <see cref="ProjectDetailRepositoryPickerTests"/>, because their behaviour is
/// a round-trip through a service and needs the real database.</para>
/// </summary>
public sealed class ProjectDetailSectionTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ProjectDetailSectionTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Editing_the_name_on_General_tells_the_page_there_are_unsaved_changes()
    {
        var edit = new ProjectDetail.EditModel { Name = "CRONUS Denmark" };
        var dirty = 0;

        var cut = _ctx.Render<ProjectDetailGeneral>(p => p
            .Add(c => c.Id, 7)
            .Add(c => c.CanManage, true)
            .Add(c => c.Edit, edit)
            .Add(c => c.OnDirty, () => dirty++));

        cut.Find("#proj-name").Change("CRONUS Sweden");

        edit.Name.Should().Be("CRONUS Sweden");
        dirty.Should().Be(1, "the page's header primary is what saves, so it has to be told");
    }

    /// <summary>
    /// Deleting is the page's write - its failure message belongs in the
    /// page-level alert and its success navigates away - so the tab only asks.
    /// The confirmation stays two steps.
    /// </summary>
    [Fact]
    public void Deleting_a_solution_takes_a_confirmation_and_then_asks_the_page()
    {
        var deletes = 0;

        var cut = _ctx.Render<ProjectDetailGeneral>(p => p
            .Add(c => c.Id, 7)
            .Add(c => c.CanManage, true)
            .Add(c => c.Edit, new ProjectDetail.EditModel())
            .Add(c => c.SolutionName, "CRONUS Denmark")
            .Add(c => c.OnDelete, () => deletes++));

        cut.FindAll("button").First(b => b.TextContent.Contains("Delete solution")).Click();
        deletes.Should().Be(0, "the first press only asks");

        cut.Markup.Should().Contain("CRONUS Denmark");
        cut.FindAll("button").First(b => b.TextContent.Contains("Yes, delete")).Click();

        deletes.Should().Be(1);
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Yes, delete"),
            "a delete that failed leaves the row back at rest beside the page's error");
    }

    [Fact]
    public void Adding_a_repository_row_tells_the_page_there_are_unsaved_changes()
    {
        var edit = new ProjectDetail.EditModel();
        var dirty = 0;

        var cut = _ctx.Render<ProjectDetailRepositories>(p => p
            .Add(c => c.CanManage, true)
            .Add(c => c.Edit, edit)
            .Add(c => c.Providers, new[] { RepositoryProvider.GitHub })
            .Add(c => c.OnDirty, () => dirty++));

        // The empty state, not a seeded blank row - the button is the affordance.
        cut.Find(".empty-state__title").TextContent.Should().Be("No repositories yet");

        cut.FindAll("button").First(b => b.TextContent.Contains("Add repository")).Click();

        edit.Repos.Should().ContainSingle();
        dirty.Should().Be(1);
        cut.FindAll("input[aria-label='Repository URL']").Should().ContainSingle();
    }

    /// <summary>
    /// The per-row errors the page re-keys off the service's positional keys
    /// still land under the row they belong to now that the table is its own
    /// component.
    /// </summary>
    [Fact]
    public void A_repository_error_renders_under_its_own_row()
    {
        var bad = new ProjectDetail.RepoRow { Url = "not-a-url" };
        var edit = new ProjectDetail.EditModel
        {
            Repos = new List<ProjectDetail.RepoRow> { new() { Url = "https://github.com/cronus/base" }, bad },
        };

        var cut = _ctx.Render<ProjectDetailRepositories>(p => p
            .Add(c => c.CanManage, true)
            .Add(c => c.Edit, edit)
            .Add(c => c.Providers, new[] { RepositoryProvider.GitHub })
            .Add(c => c.RepoErrors, new Dictionary<ProjectDetail.RepoRow, Dictionary<string, string>>
            {
                [bad] = new() { ["Url"] = "That doesn't look like a repository URL." },
            }));

        var rows = cut.FindAll("tbody tr");
        rows.Count.Should().Be(2);
        rows[0].QuerySelectorAll(".field-error").Should().BeEmpty();
        rows[1].QuerySelector(".field-error")!.TextContent
            .Should().Contain("That doesn't look like a repository URL.");
    }
}
