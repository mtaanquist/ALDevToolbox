using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the code pane's palette, which is split across two files that no
/// compiler checks against each other: <c>code-editor.js</c> names a CSS class
/// per syntax tag, and <c>tools.css</c> gives each of those classes a colour
/// from the <c>--code-*</c> tokens. Break either half and the editor still
/// mounts, still highlights, and just paints in the browser's default ink --
/// which is exactly what it did before this layer existed, so nothing looks
/// obviously wrong.
///
/// The other half of the guard is negative: the editor used to ship
/// CodeMirror's own <c>defaultHighlightStyle</c> on light and the
/// <c>one-dark</c> theme on dark. Either creeping back would silently
/// out-specify the token palette on one theme only.
/// </summary>
public sealed class CodePaletteTests
{
    [Fact]
    public void Every_syntax_class_the_editor_names_is_tinted_in_the_stylesheet()
    {
        var declared = HighlightClasses();
        declared.Should().NotBeEmpty(because: "alHighlightStyle assigns its own class names");

        var css = Read("ALDevToolbox/wwwroot/tools.css");
        foreach (var cls in declared)
        {
            Rule(css, "." + cls).Should().NotBeNull(
                because: $"code-editor.js paints tokens with .{cls}, so tools.css has to colour it");
        }
    }

    [Fact]
    public void Every_syntax_class_takes_its_colour_from_a_code_token()
    {
        var css = Read("ALDevToolbox/wwwroot/tools.css");
        foreach (var cls in HighlightClasses())
        {
            var body = Rule(css, "." + cls)!;
            body.Should().MatchRegex(@"color:\s*var\(--(code|danger)-",
                because: $".{cls} must follow the theme's tokens, not a literal colour");
        }
    }

    [Fact]
    public void The_editor_no_longer_ships_CodeMirrors_own_palettes()
    {
        var js = Read("ALDevToolbox/wwwroot/code-editor.js");
        js.Should().NotContain("defaultHighlightStyle",
            because: "the stock palette is not the app's palette");
        js.Should().NotContain("one-dark",
            because: "a second palette on dark only is how the two themes drifted apart");
    }

    [Fact]
    public void Every_editor_mount_uses_the_token_highlight_style()
    {
        var js = Read("ALDevToolbox/wwwroot/code-editor.js");
        var calls = Regex.Matches(js, @"syntaxHighlighting\(\s*([A-Za-z]+)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        calls.Should().HaveCountGreaterOrEqualTo(3,
            because: "the editable mount, the read-only mount and the compare mount each install one");
        calls.Should().OnlyContain(name => name == "alHighlightStyle");
    }

    /// <summary>
    /// The <c>--code-*</c> tokens were declared for this and sat unused for
    /// several releases. If a theme block loses one, the class that reads it
    /// falls back to inheriting and the token stops meaning anything.
    /// </summary>
    [Fact]
    public void Every_code_token_the_palette_reads_is_declared_in_both_themes()
    {
        var css = Read("ALDevToolbox/wwwroot/tools.css");
        var tokens = HighlightClasses()
            .Select(cls => Rule(css, "." + cls)!)
            .SelectMany(body => Regex.Matches(body, @"var\((--code-[a-z]+)\)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        tokens.Should().NotBeEmpty();

        var tokensCss = Read("ALDevToolbox/wwwroot/tokens.css");
        foreach (var token in tokens)
        {
            // Once in the light block, once under prefers-color-scheme: dark,
            // once under [data-theme="dark"] — the three-state rule.
            Regex.Matches(tokensCss, Regex.Escape(token) + @"\s*:").Count
                .Should().BeGreaterOrEqualTo(3,
                    because: $"{token} needs a value in the light, media-dark and forced-dark blocks");
        }
    }

    /// <summary>
    /// Every class name <c>alHighlightStyle</c> hands to CodeMirror.
    /// </summary>
    private static List<string> HighlightClasses()
    {
        var js = Read("ALDevToolbox/wwwroot/code-editor.js");
        var start = js.IndexOf("const alHighlightStyle", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var end = js.IndexOf("]);", start, StringComparison.Ordinal);
        var block = js[start..end];
        return Regex.Matches(block, @"class:\s*""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The declaration body of the first rule whose selector list contains
    /// <paramref name="selector"/> exactly, or null when there is none.
    /// Comment-stripped first so a class named in prose doesn't count.
    /// </summary>
    private static string? Rule(string css, string selector)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selectors = m.Groups[1].Value.Split(',').Select(s => s.Trim());
            if (selectors.Any(s => s == selector)) return m.Groups[2].Value;
        }
        return null;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull(because: "the tests run from inside the repo");
        return dir!.FullName;
    }
}
