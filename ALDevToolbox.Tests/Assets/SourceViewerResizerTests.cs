using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the outline resizer against outliving the element it was wired to.
///
/// Blazor's enhanced navigation re-uses DOM nodes across routes. Navigating
/// from the Object Explorer's file viewer to another source-viewer page hands
/// the resizer's &lt;div&gt; to the next page as a completely different element,
/// with the listeners still attached. In issue #535 it came back as the Compare
/// tool's right pane: the pointerdown handler's unconditional
/// <c>preventDefault()</c> swallowed every click there, so the pane never took
/// focus and the user could not type into it — the tool's second input was
/// simply dead until a full reload.
///
/// The listeners cannot be removed from inside the closure, so each one
/// re-checks that it is still driving a live resizer. That check is invisible
/// at the call site and easy to drop in a refactor, and losing it does not
/// break anything on the file viewer itself — only on the page you land on
/// next. Hence a test rather than a comment.
/// </summary>
public sealed class SourceViewerResizerTests
{
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    [Fact]
    public void The_resizer_checks_it_is_still_a_resizer_before_swallowing_input()
    {
        var resizer = Between(Read(ViewerJs), "function wireOutlineResizer", "\n}\n");

        resizer.Should().Contain("source-viewer__resizer\")",
            because: "a re-purposed node no longer carries the resizer's class — that is what tells the two apart");

        // Every handler that consumes an event the page needs has to be gated:
        // pointerdown (preventDefault swallows the click that focuses an editor)
        // and keydown (preventDefault swallows ArrowLeft/ArrowRight).
        foreach (var handler in new[] { "\"pointerdown\"", "\"keydown\"" })
        {
            var body = Between(resizer, $"handle.addEventListener({handler}", "});");
            body.Should().Contain("isLiveResizer()",
                because: $"the {handler} handler preventDefaults, so a stale copy of it blocks input on whatever page reuses the node");
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be a complete block");
        return text[from..to];
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
