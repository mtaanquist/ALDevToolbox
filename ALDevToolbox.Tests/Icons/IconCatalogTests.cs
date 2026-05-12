using System.Text.RegularExpressions;
using ALDevToolbox.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Icons;

/// <summary>
/// Guards the vendored Lucide replacement for the unmaintained Lucide.Blazor
/// package (issue #47). The third test is the important one — it's the build-
/// time check that would have caught PR #46's <c>users-round</c> render-time
/// <c>KeyNotFoundException</c> before it shipped.
/// </summary>
public sealed class IconCatalogTests
{
    private static IconCatalog NewCatalog() => new(NullLogger<IconCatalog>.Instance);

    [Fact]
    public void Known_icon_returns_non_empty_inner_svg()
    {
        var catalog = NewCatalog();

        var inner = catalog.GetInnerSvg("users-round");

        inner.Should().NotBeNull();
        inner!.Should().NotBeNullOrWhiteSpace();
        // Lucide icons are composed of <path>, <circle>, <line>, <rect>, <polyline>
        // and <ellipse> children. If the inner blob doesn't contain at least one
        // of those, the regex extraction in IconCatalog has regressed.
        inner.Should().MatchRegex("<(path|circle|line|rect|polyline|ellipse)\\b");
        inner.Should().NotContain("<svg");
    }

    [Fact]
    public void Unknown_icon_returns_null_instead_of_throwing()
    {
        var catalog = NewCatalog();

        // The whole point of the swap: lucide.dev's current name for users-round
        // didn't exist in the old package and crashed every authenticated render.
        // The replacement must degrade gracefully.
        var act = () => catalog.GetInnerSvg("this-icon-does-not-exist");

        act.Should().NotThrow();
        catalog.GetInnerSvg("this-icon-does-not-exist").Should().BeNull();
    }

    [Fact]
    public void Catalogue_contains_every_icon_name_referenced_by_a_razor_call_site()
    {
        var catalog = NewCatalog();
        var available = new HashSet<string>(catalog.Names, StringComparer.Ordinal);

        var referenced = CollectReferencedIconNames();
        referenced.Should().NotBeEmpty("the test couldn't find any <Icon Name=...> usage — has the component moved?");

        var missing = referenced
            .Where(kv => !available.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var report = string.Join("; ", missing.Select(kv => $"{kv.Key} (in {string.Join(", ", kv.Value)})"));

        missing.Should().BeEmpty(
            "every icon name used in *.razor must have a vendored SVG under Resources/Icons/. " +
            "Missing: {0}", report);
    }

    private static IReadOnlyDictionary<string, List<string>> CollectReferencedIconNames()
    {
        var componentsDir = FindComponentsDir();
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Two patterns we need to cover:
        //   <Icon Name="folder-plus" ...>
        //   ConfirmIcon="trash-2"
        var literal = new Regex("(?:<Icon\\s+[^>]*?Name|ConfirmIcon)=\"(?<name>[a-z0-9-]+)\"", RegexOptions.Compiled);
        // And the conditional form: Name="@(cond ? "a" : "b")" — both branches.
        var conditional = new Regex("Name=\"@\\([^?]+\\?\\s*\"(?<a>[a-z0-9-]+)\"\\s*:\\s*\"(?<b>[a-z0-9-]+)\"\\s*\\)\"", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(componentsDir, file);
            var content = File.ReadAllText(file);
            foreach (Match m in literal.Matches(content)) Record(hits, m.Groups["name"].Value, rel);
            foreach (Match m in conditional.Matches(content))
            {
                Record(hits, m.Groups["a"].Value, rel);
                Record(hits, m.Groups["b"].Value, rel);
            }
        }

        return hits;
    }

    private static void Record(Dictionary<string, List<string>> hits, string name, string file)
    {
        if (!hits.TryGetValue(name, out var files))
        {
            files = new List<string>();
            hits[name] = files;
        }
        if (!files.Contains(file)) files.Add(file);
    }

    private static string FindComponentsDir()
    {
        // Walk up from the test binary until we find the repo root marker, then
        // descend into the app's Components folder. Works for both `dotnet test`
        // (bin/Debug/net10.0) and IDE runners.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ALDevToolbox.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("could not locate repo root (looking for ALDevToolbox.slnx)");
        var components = Path.Combine(dir!.FullName, "ALDevToolbox", "Components");
        Directory.Exists(components).Should().BeTrue("expected Components folder at {0}", components);
        return components;
    }
}
