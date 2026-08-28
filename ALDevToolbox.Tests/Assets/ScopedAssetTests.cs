using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the pairing between a component and its scoped companion files.
///
/// Blazor CSS isolation ties a <c>.razor.css</c> sheet to the component it is
/// NAMED after: the build stamps that component's elements with a scope
/// attribute and rewrites every selector in the sheet to require it. Move the
/// markup into a differently named component and the sheet still compiles,
/// still ships, and matches nothing - no build error, no console error, the
/// page just goes flat. #613 did exactly that to the Object Explorer landing
/// page (ReleasesBrowser.razor.css stayed behind when the markup moved into
/// ReleasesBrowserView.razor) and it shipped in two patch releases before a
/// screenshot caught it; #618 is the fix and this is the rule it restores.
///
/// The check is deliberately loose - AT LEAST ONE class from the sheet must
/// appear in the sibling markup, not all of them - because components compose
/// class names at runtime (<c>rp-app__state--@state</c>, JS-toggled
/// <c>is-dismissed</c>) and a strict test would police those. Whole-sheet
/// orphaning, the failure that actually happens when markup moves, scores
/// zero and is what this catches.
/// </summary>
public class ScopedAssetTests
{
    [Fact]
    public void Every_scoped_stylesheet_has_a_sibling_component()
    {
        foreach (var css in Companions("*.razor.css"))
        {
            File.Exists(css[..^4]).Should().BeTrue(
                because: $"{Relative(css)} is scoped by name to a component that does not exist, "
                       + "so none of its rules can match anything - move or delete it");
        }
    }

    [Fact]
    public void Every_scoped_stylesheet_styles_its_own_component()
    {
        foreach (var css in Companions("*.razor.css"))
        {
            var razor = css[..^4];
            if (!File.Exists(razor)) continue; // reported by the test above

            var sheet = StripCssComments(File.ReadAllText(css));
            // ::deep reaches into child components by design; a sheet that is
            // entirely ::deep legitimately names no class of its own.
            if (sheet.Contains("::deep")) continue;

            var classes = ClassSelectors(sheet).ToList();
            if (classes.Count == 0) continue; // element-only rules (body, table...) cannot be checked by name

            var markup = StripRazorComments(File.ReadAllText(razor));
            classes.Any(c => markup.Contains(c, StringComparison.Ordinal)).Should().BeTrue(
                because: $"{Relative(css)} names {classes.Count} classes and {Path.GetFileName(razor)} "
                       + "renders none of them - CSS isolation scopes the sheet to the component it "
                       + "is named after, so if the markup moved to another component the sheet has "
                       + "to move with it (see #618)");
        }
    }

    [Fact]
    public void Every_companion_script_is_imported_by_its_component()
    {
        foreach (var js in Companions("*.razor.js"))
        {
            var razor = js[..^3];
            File.Exists(razor).Should().BeTrue(
                because: $"{Relative(js)} is a companion script for a component that does not exist");

            var markup = File.ReadAllText(razor);
            markup.Contains(Path.GetFileName(js), StringComparison.Ordinal).Should().BeTrue(
                because: $"{Path.GetFileName(razor)} never imports {Path.GetFileName(js)}, so the script "
                       + "ships and nothing loads it - the same silent orphaning as a stranded stylesheet");
        }
    }

    // ── Helpers (mirroring DetailHeadTests) ────────────────────────────

    private static IEnumerable<string> Companions(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "ALDevToolbox/Components"), pattern,
            SearchOption.AllDirectories);

    private static string Relative(string full) =>
        Path.GetRelativePath(Root(), full).Replace('\\', '/');

    private static string StripCssComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string StripRazorComments(string razor) =>
        Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);

    /// <summary>
    /// Every <c>.class</c> token in a selector position: outside declaration
    /// blocks, so a <c>.5</c> in a value or a class inside a <c>url()</c> is
    /// not mistaken for one.
    /// </summary>
    private static IEnumerable<string> ClassSelectors(string css)
    {
        var selectorsOnly = Regex.Replace(css, @"\{[^{}]*\}", " ");
        return Regex.Matches(selectorsOnly, @"\.(?<c>-?[_a-zA-Z][\w-]*)")
            .Select(m => m.Groups["c"].Value)
            .Distinct();
    }

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
