using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the click-to-collapse behaviour and the .gitkeep-only default-
/// collapsed heuristic in <see cref="FolderTreeNode"/>. The component's
/// header comment calls out that user toggles must survive parent
/// re-renders — that contract is pinned by the IsExpanded property's
/// _userExpanded latching and verified here.
/// </summary>
public sealed class FolderTreeNodeTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public FolderTreeNodeTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Folder_with_children_renders_a_toggle_button_and_starts_expanded_by_default()
    {
        var node = PreviewNode.Folder("src", new[]
        {
            PreviewNode.File("Codeunit.al"),
            PreviewNode.File("Page.al"),
        });

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        var toggle = cut.Find("button.ftp__row--toggle");
        toggle.GetAttribute("aria-label").Should().Be("Collapse",
            "default-expanded folders render the toggle as a 'Collapse' affordance");
        cut.Find("li").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("ul.ftp__children > li").Should().HaveCount(2);
    }

    [Fact]
    public void Folder_containing_only_a_gitkeep_starts_collapsed()
    {
        var node = PreviewNode.Folder("empty", new[] { PreviewNode.File(".gitkeep") });

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        cut.Find("li").GetAttribute("aria-expanded").Should().Be("false",
            "a folder whose only child is .gitkeep collapses by default — keeps the "
            + "preview readable when extensions ship empty placeholder folders");
        cut.FindAll("ul.ftp__children").Should().BeEmpty(
            "the children are not rendered when collapsed — the chevron flips, "
            + "but the nested <ul> stays out of the DOM until the user expands");
    }

    [Fact]
    public void Clicking_the_toggle_flips_aria_expanded_and_locks_user_choice()
    {
        var node = PreviewNode.Folder("empty", new[] { PreviewNode.File(".gitkeep") });

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        cut.Find("li").GetAttribute("aria-expanded").Should().Be("false");

        cut.Find("button.ftp__row--toggle").Click();

        cut.Find("li").GetAttribute("aria-expanded").Should().Be("true",
            "clicking expand must override the .gitkeep-only default — once the "
            + "user has expressed an intent the component latches it");
        cut.FindAll("ul.ftp__children > li").Should().HaveCount(1);
    }

    [Fact]
    public void File_nodes_render_a_placeholder_chevron_and_no_toggle()
    {
        var node = PreviewNode.File("README.md");

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        cut.FindAll("button.ftp__row--toggle").Should().BeEmpty(
            "files are leaves — there is nothing to expand");
        cut.Find("span.ftp__chevron--placeholder").Should().NotBeNull();
        cut.Find("li").GetAttribute("aria-expanded").Should().BeNull(
            "files don't carry aria-expanded — only treeitems that can expand do");
    }

    [Fact]
    public void Newly_added_nodes_render_the_new_badge()
    {
        var node = PreviewNode.File("Newly.al") with { IsNew = true };

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        var badge = cut.Find("span.ftp__badge--new");
        badge.TextContent.Should().Be("new");
        badge.GetAttribute("aria-label").Should().Be("newly added");
    }

    [Theory]
    [InlineData(PreviewNodeKind.Workspace, "ftp__node--workspace")]
    [InlineData(PreviewNodeKind.Extension, "ftp__node--extension")]
    [InlineData(PreviewNodeKind.Folder, "ftp__node--folder")]
    [InlineData(PreviewNodeKind.File, "ftp__node--file")]
    public void Node_class_reflects_kind(PreviewNodeKind kind, string expectedClass)
    {
        var node = new PreviewNode("x", kind, Array.Empty<PreviewNode>());

        var cut = _ctx.RenderComponent<FolderTreeNode>(p => p.Add(c => c.Node, node));

        cut.Find("li").GetAttribute("class").Should().Contain(expectedClass);
    }
}
