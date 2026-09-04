using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the geometry of the toast stack, which is out of flow but is mounted
/// in flow.
///
/// A page mounts <c>&lt;ToastHost /&gt;</c> at its root, so the stack is a
/// direct child of <c>.app__content-inner</c> alongside the page itself. It is
/// <c>position: fixed</c>, so it is not a grid item and the frame's alignment
/// never reaches it - but a frame rule that DECLARES a size on every root child
/// does, and on a fixed box a percentage resolves against the viewport.
///
/// Both axes have now done this. <c>width: 100%</c> in app.css stretched the
/// 400px pill into a full-width bar along the bottom of the window, and the fix
/// there was to name the stack in a <c>:not()</c>. Then the power-tool frame
/// added <c>height: 100%</c> to every root child for the panes that have to
/// fill the column, and the Translator's save confirmation became a message box
/// the height of the screen (#720) - the stack grew to the viewport and
/// stretched its single grid row with it.
///
/// So one test per axis, each written against the rule that would bring the
/// bug back rather than against the fix.
/// </summary>
public sealed class ToastStackTests
{
    private const string App = "ALDevToolbox/wwwroot/app.css";
    private const string Power = "ALDevToolbox/wwwroot/pages-power.css";
    private const string Scoped = "ALDevToolbox/Components/Shared/ToastHost.razor.css";

    [Fact]
    public void The_page_frame_does_not_stretch_the_stack_across_the_window()
    {
        var rule = Rule(Read(App), ".app__content-inner > *");
        rule.Should().BeNull(
            because: "`width: 100%` on the fixed stack resolves against the viewport, so an "
                   + "unqualified rule over root children renders the toast as a full-width bar "
                   + $"along the bottom of the window - keep the `:not(.toast-stack)` in {App}");
    }

    [Fact]
    public void The_power_tool_frame_does_not_stretch_the_stack_down_the_window()
    {
        // The frame legitimately sizes its own panes; what it cannot do is size
        // an overlay mounted next to them. The scoped sheet takes the height
        // back rather than the frame excluding it, so a stack mounted inside
        // the next frame with a rule like this one is covered too.
        Rule(Read(Power), ".app__content-inner:has(.pw) > *").Should().Contain("height: 100%",
            because: "this test only matters while a frame still sizes every root child; "
                   + "if that goes upstream, this pair of tests goes with it");

        Rule(Read(Scoped), ".toast-stack").Should()
            .NotBeNull(because: "the stack is where the frame's height lands")
            .And.Contain("height: auto",
                because: "the stack is a fixed pill that sizes to the one message in it; left at "
                       + "the frame's `height: 100%` it fills the viewport and stretches its grid "
                       + "row, which is a toast the height of the screen (#720)");
    }

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            yield return (Regex.Replace(m.Groups["sel"].Value, @"\s+", " ").Trim(), m.Groups["body"].Value);
        }
    }

    private static string? Rule(string css, string selector) =>
        Rules(css).FirstOrDefault(r => r.Selector.Split(',')
            .Any(s => Regex.Replace(s, @"\s+", " ").Trim() == selector)).Body;

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
