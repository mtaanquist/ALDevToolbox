using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the two things about the design layer that fail silently.
///
/// The first is the &lt;link&gt; order in <c>App.razor</c>. Load order is the
/// migration mechanism: a design sheet has to load BEFORE the legacy sheets so
/// a not-yet-migrated page can still override it, and AFTER the ones it builds
/// on. A sheet dropped into <c>wwwroot/</c> and never linked is inert; a sheet
/// linked in the wrong place shadows or is shadowed by the wrong neighbour.
/// Neither produces an error anywhere -- the page just looks subtly wrong, and
/// the CSS is right there in the file when you go looking.
///
/// The second is drift between the two copies of each shared sheet. The rule is
/// that a correction goes upstream to the design project and comes back down to
/// both <c>.design/handoff/</c> and <c>wwwroot/</c>, so a fix is never made to
/// one copy alone. Byte-identical is the only version of that rule a test can
/// check.
/// </summary>
public sealed class StylesheetLoadOrderTests
{
    /// <summary>
    /// The design layer, in the order the design system stacks it: tokens, then
    /// the shared components, then the shell, then one sheet per archetype
    /// family. pages-power.css sits after pages-forms.css because its compare
    /// section extends the .diff block declared there.
    ///
    /// Then the sheets that deliberately override it. base/tools/admin are the
    /// legacy remainder and shrink every PR; code-editor.css and
    /// source-viewer.css are NOT legacy and are not going anywhere - the first
    /// styles DOM CodeMirror builds at runtime, the second is the Object
    /// Explorer's own composition on archetype 10. They sit after the design
    /// layer because that is what they extend (PR 17b).
    /// </summary>
    private static readonly string[] Expected =
    [
        "fonts.css", "tokens.css", "components.css", "shell.css",
        "pages.css", "pages-forms.css", "pages-power.css", "pages-content.css",
        "base.css", "tools.css", "code-editor.css", "source-viewer.css", "admin.css",
    ];

    [Fact]
    public void Every_sheet_in_wwwroot_is_linked_from_App_razor()
    {
        var onDisk = Directory.EnumerateFiles(Wwwroot(), "*.css")
            .Select(Path.GetFileName)
            .Where(n => n != "ALDevToolbox.styles.css")  // generated from the scoped sheets
            .Order()
            .ToList();

        Linked().Should().BeEquivalentTo(onDisk,
            "a sheet nobody links is inert, and nothing else would say so");
    }

    [Fact]
    public void Sheets_load_in_the_documented_order()
    {
        Linked().Should().Equal(Expected,
            "the design sheets must load before the legacy ones (so a page that " +
            "has not migrated can still override) and after the sheets they build on");
    }

    [Theory]
    [InlineData("tokens.css")]
    [InlineData("components.css")]
    [InlineData("shell.css")]
    [InlineData("pages.css")]
    [InlineData("pages-forms.css")]
    [InlineData("pages-power.css")]
    [InlineData("pages-content.css")]
    public void Shared_sheets_match_their_handoff_copy_byte_for_byte(string sheet)
    {
        var app = File.ReadAllBytes(Path.Combine(Wwwroot(), sheet));
        var handoff = File.ReadAllBytes(Path.Combine(Root(), ".design", "handoff", sheet));

        app.Should().Equal(handoff,
            "{0} has two copies and a correction to one is a correction to both; " +
            "push the change to the design project and pull it back down, do not " +
            "patch a single copy", sheet);
    }

    /// <summary>The stylesheet links from App.razor, in document order.</summary>
    private static List<string> Linked()
    {
        var app = File.ReadAllText(Path.Combine(Root(), "ALDevToolbox", "Components", "App.razor"));
        return Regex.Matches(app, """<link rel="stylesheet" href="@Assets\["([^"]+)"\]""")
            .Select(m => m.Groups[1].Value)
            .Where(n => n != "ALDevToolbox.styles.css")
            .ToList();
    }

    private static string Wwwroot() => Path.Combine(Root(), "ALDevToolbox", "wwwroot");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("could not locate repo root (looking for ALDevToolbox.slnx)");
        return dir!.FullName;
    }
}
