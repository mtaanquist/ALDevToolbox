using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the pane-split handles against outliving the element they were wired
/// to.
///
/// Blazor's enhanced navigation re-uses DOM nodes across routes. Navigating
/// from a source-viewer page to another one hands a split handle's &lt;div&gt;
/// to the next page as a completely different element, with the listeners
/// still attached. In issue #535 it came back as the right pane of the
/// standalone diff tool (/diff): the pointerdown handler's unconditional
/// <c>preventDefault()</c> swallowed every click there, so the pane never took
/// focus and the user could not type into it — the tool's second input was
/// simply dead until a full reload.
///
/// <c>__splitBound</c> does not help: it stops the same handle being bound
/// twice, but the stale listeners from the previous page are already attached
/// and keep firing. The listeners cannot be removed from inside the closure,
/// so each one re-checks that it is still driving a live split handle. That
/// check is invisible at the call site and easy to drop in a refactor, and
/// losing it does not break anything on the page that wired it — only on the
/// page you land on next. Hence a test rather than a comment.
/// </summary>
public sealed class SourceViewerResizerTests
{
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";

    [Fact]
    public void A_split_handle_checks_it_is_still_a_split_handle_before_swallowing_input()
    {
        var split = Between(Read(ViewerJs), "function wireSplit", "\n}\n");

        split.Should().Contain("\"pw-split\"",
            because: "a re-purposed node no longer carries the handle's class — that is what tells the two apart");

        // Every handler that consumes an event the page needs has to be gated:
        // pointerdown (preventDefault swallows the click that focuses an editor)
        // and keydown (preventDefault swallows ArrowLeft/ArrowRight).
        foreach (var handler in new[] { "\"pointerdown\"", "\"keydown\"" })
        {
            var body = Between(split, $"handle.addEventListener({handler}", "});");
            body.Should().Contain("isLiveHandle()",
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
