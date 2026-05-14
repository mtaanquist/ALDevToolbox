using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Wrapper around <see cref="FolderTreeNode"/>. The contract is small:
/// null Root → render nothing (loading); non-null Root → wrap a single
/// recursive node in <c>&lt;ul class="ftp ftp--root" role="tree"&gt;</c>.
/// </summary>
public sealed class FolderTreePreviewTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public FolderTreePreviewTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Null_root_renders_nothing_so_the_caller_can_show_a_loading_state_above_it()
    {
        var cut = _ctx.RenderComponent<FolderTreePreview>(p => p.Add(c => c.Root, null));

        cut.Markup.Trim().Should().BeEmpty(
            "the New Workspace / New Extension pages render a loading affordance "
            + "while the tree is null; the component itself must stay silent");
    }

    [Fact]
    public void Non_null_root_renders_a_role_tree_ul_containing_a_single_recursive_node()
    {
        var root = new PreviewNode("Workspace", PreviewNodeKind.Workspace, new[]
        {
            PreviewNode.File("README.md"),
        });

        var cut = _ctx.RenderComponent<FolderTreePreview>(p => p.Add(c => c.Root, root));

        var ul = cut.Find("ul.ftp.ftp--root");
        ul.GetAttribute("role").Should().Be("tree",
            "ARIA role=tree is the screen-reader contract the FolderTreeNode "
            + "treeitem children rely on");
        cut.FindAll("ul.ftp--root > li").Should().HaveCount(1,
            "the wrapper renders exactly one root <li>; recursion happens inside it");
    }
}
