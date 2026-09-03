using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Compare-scope results table. A release-to-release file diff runs to
/// thousands of rows, and rendering one <c>&lt;tr&gt;</c> each — with a state
/// glyph and an actions cell inside — costs the same again in render-tree nodes
/// held on the circuit plus a first-paint payload down SignalR. The table is
/// virtualized so only a window is ever in the markup, and it says out loud when
/// the diff was capped rather than listing a silently short set (#685).
/// </summary>
public sealed class OeCompareResultsTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public OeCompareResultsTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<ObjectExplorerLinks>();
    }

    public void Dispose() => _ctx.Dispose();

    private static List<ReleaseCompareFileRow> Rows(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ReleaseCompareFileRow(
                Guid.Empty, "Base Application", $"src/File{i:D5}.al", "modified", i, i + 1_000_000))
            .ToList();

    [Fact]
    public void A_huge_file_diff_renders_only_a_window_of_rows()
    {
        var cut = _ctx.Render<OeCompareResults>(p => p
            .Add(c => c.CompareRight, "42")
            .Add(c => c.CompareBy, "files")
            .Add(c => c.FileResults, Rows(3_000)));

        // Virtualize's own spacer rows carry the height of everything not
        // rendered, so the scrollbar is right without the rows existing.
        cut.FindAll("tbody tr[aria-hidden]").Should().NotBeEmpty(
            "the table body is wrapped in Virtualize, whose spacers are tr elements");

        var rendered = cut.FindAll("tbody tr:not([style])").Count;
        rendered.Should().BeGreaterThan(0, "the visible window is still rendered");
        rendered.Should().BeLessThan(3_000,
            because: "the whole diff must never be materialised into the markup");

        // The per-row furniture survives virtualization.
        cut.FindAll(".data-table__state").Should().NotBeEmpty();
        cut.FindAll("tbody a.btn").Should().NotBeEmpty();
    }

    [Fact]
    public void A_capped_diff_says_it_is_showing_only_the_first_rows()
    {
        var cut = _ctx.Render<OeCompareResults>(p => p
            .Add(c => c.CompareRight, "42")
            .Add(c => c.FileResults, Rows(5_000))
            .Add(c => c.FileResultsTruncated, true));

        var caption = cut.Find("p.u-muted").TextContent;
        caption.Should().Contain("first 5,000");
        caption.Should().Contain("more than");
        caption.Should().Contain("narrow the comparison");
    }

    [Fact]
    public void An_uncapped_diff_states_the_plain_count()
    {
        var cut = _ctx.Render<OeCompareResults>(p => p
            .Add(c => c.CompareRight, "42")
            .Add(c => c.FileResults, Rows(3)));

        var caption = cut.Find("p.u-muted").TextContent;
        caption.Should().Contain("Showing 3 changed files");
        caption.Should().NotContain("first");
    }
}
