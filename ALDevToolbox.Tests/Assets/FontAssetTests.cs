using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the vendored Selawik web font, the same way <c>IconCatalogTests</c>
/// guards the vendored Lucide SVGs: a missing file here is a render-time
/// regression nobody notices until the app is open on a non-Windows machine.
///
/// The font-stack ordering test is the important one. The order is a licensing
/// decision, not a preference — see <c>.design/design-migration.md</c>. Segoe UI
/// cannot be self-hosted, so it is named first and resolved from the user's own
/// Windows install; Selawik (Microsoft's OFL metric-compatible replacement) sits
/// behind it purely as the off-Windows fallback. Reversing that order would make
/// every Windows user download a font they already have a better copy of.
/// </summary>
public sealed class FontAssetTests
{
    [Fact]
    public void Every_font_referenced_by_fonts_css_is_vendored()
    {
        var wwwroot = FindWwwroot();
        var css = File.ReadAllText(Path.Combine(wwwroot, "fonts.css"));

        var referenced = Regex.Matches(css, @"url\((?<q>[""']?)(?<path>[^""')]+)\k<q>\)")
            .Select(m => m.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        referenced.Should().NotBeEmpty("fonts.css should declare at least one @font-face src");

        var missing = referenced
            .Where(p => !File.Exists(Path.Combine(wwwroot, p.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        missing.Should().BeEmpty(
            "every url() in fonts.css must resolve to a file under wwwroot/. Missing: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void Vendored_font_ships_its_open_font_licence()
    {
        // OFL-1.1 requires the licence to travel with the font files.
        var licence = Path.Combine(FindWwwroot(), "fonts", "Selawik-LICENSE.txt");

        File.Exists(licence).Should().BeTrue(
            "OFL-1.1 requires the licence text to accompany the font binaries at {0}", licence);
        File.ReadAllText(licence).Should().Contain("SIL Open Font License");
    }

    [Fact]
    public void Font_stack_asks_for_segoe_before_the_hosted_fallback()
    {
        var tokens = File.ReadAllText(Path.Combine(FindWwwroot(), "tokens.css"));

        var match = Regex.Match(tokens, @"--font-sans:\s*(?<value>[^;]+);");
        match.Success.Should().BeTrue("tokens.css must declare --font-sans");

        var stack = match.Groups["value"].Value;
        var segoe = stack.IndexOf("Segoe UI", StringComparison.Ordinal);
        var selawik = stack.IndexOf("Selawik", StringComparison.Ordinal);
        var systemUi = stack.IndexOf("system-ui", StringComparison.Ordinal);

        segoe.Should().BeGreaterThanOrEqualTo(0, "Segoe UI is the design system's specified face");
        selawik.Should().BeGreaterThan(segoe,
            "Selawik is the off-Windows fallback only — ahead of Segoe it would make every " +
            "Windows user download a font they already have a better copy of");
        systemUi.Should().BeGreaterThan(selawik,
            "system-ui catches the scripts Selawik does not cover (Cyrillic, Greek, Vietnamese, CJK)");
    }

    /// <summary>
    /// Walks up from the test binary to the repo root marker, then down into the
    /// app's wwwroot. Mirrors <c>IconCatalogTests.FindComponentsDir</c> so both
    /// work under <c>dotnet test</c> and IDE runners alike.
    /// </summary>
    private static string FindWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("could not locate repo root (looking for ALDevToolbox.slnx)");
        var wwwroot = Path.Combine(dir!.FullName, "ALDevToolbox", "wwwroot");
        Directory.Exists(wwwroot).Should().BeTrue("expected wwwroot folder at {0}", wwwroot);
        return wwwroot;
    }
}
