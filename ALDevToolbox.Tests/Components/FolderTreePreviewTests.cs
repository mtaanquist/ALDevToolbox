using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The live folder-tree preview on New Workspace / New Extension, now the
/// design system's flat <c>.tree</c> — rows indented by a <c>--d</c> depth
/// custom property rather than nested lists, which is why the recursion moved
/// into the component and <c>FolderTreeNode</c> went away.
///
/// The behaviour that survived the move is what's pinned here: null root stays
/// silent, folders collapse, a folder holding only a <c>.gitkeep</c> starts
/// closed, an explicit toggle sticks, and <c>IsNew</c> is still marked.
/// </summary>
public sealed class FolderTreePreviewTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public FolderTreePreviewTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Null_root_renders_nothing_so_the_caller_can_show_a_loading_state_above_it()
    {
        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, null));

        cut.Markup.Trim().Should().BeEmpty(
            "the New Workspace / New Extension pages render a loading affordance "
            + "while the tree is null; the component itself must stay silent");
    }

    [Fact]
    public void Rows_are_flat_and_carry_their_depth_as_the_d_custom_property()
    {
        var root = PreviewNode.Folder("Workspace", new[]
        {
            PreviewNode.Extension("CRONUS.Core", new[] { PreviewNode.File("app.json") }),
        });

        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        cut.Find("div.tree").GetAttribute("role").Should().Be("tree");
        var rows = cut.FindAll(".tree__row");
        rows.Should().HaveCount(3, "root, extension and file are siblings in a flat list");
        rows.Select(r => r.GetAttribute("style")).Should().Equal("--d: 0", "--d: 1", "--d: 2");
        rows[1].ClassList.Should().Contain("tree__row--gen",
            "an extension folder is the accented row the legend explains");
        rows[2].ClassList.Should().Contain("tree__row--file");
    }

    [Fact]
    public void A_folder_with_children_is_a_toggle_and_starts_expanded()
    {
        var root = PreviewNode.Folder("src", new[] { PreviewNode.File("Item.al") });

        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        var toggle = cut.Find("button.tree__row--toggle");
        toggle.GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".tree__row").Should().HaveCount(2, "the child is visible while expanded");
    }

    [Fact]
    public void A_folder_holding_only_a_gitkeep_starts_collapsed()
    {
        var root = PreviewNode.Folder("empty", new[] { PreviewNode.File(".gitkeep") });

        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        cut.Find("button.tree__row--toggle").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".tree__row").Should().HaveCount(1,
            "a folder whose only content is a placeholder has nothing worth showing");
    }

    [Fact]
    public void Clicking_a_folder_row_flips_it_and_the_choice_sticks()
    {
        var root = PreviewNode.Folder("empty", new[] { PreviewNode.File(".gitkeep") });
        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        cut.Find("button.tree__row--toggle").Click();
        cut.Find("button.tree__row--toggle").GetAttribute("aria-expanded").Should().Be("true");

        // Re-rendering is what typing in the form does; the user's choice must
        // outlive it rather than snapping back to the .gitkeep default.
        cut.Render();
        cut.Find("button.tree__row--toggle").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void Newly_added_nodes_are_marked_in_the_row_meta()
    {
        var root = PreviewNode.Folder("Workspace", new[]
        {
            PreviewNode.File("Newly.al") with { IsNew = true },
        });

        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        cut.Find(".tree__row--file .tree__meta").TextContent.Trim().Should().Be("new");
    }

    [Fact]
    public void The_legend_explains_what_the_accented_folders_are()
    {
        var root = PreviewNode.Folder("Workspace");

        var cut = _ctx.Render<FolderTreePreview>(p => p.Add(c => c.Root, root));

        cut.Find(".tree__key--gen").TextContent.Should().Contain("Generated extension");
    }
}
