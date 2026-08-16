using System.Text.RegularExpressions;
using FluentAssertions;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the mechanism the design-system migration runs on, and the bug class
/// it keeps producing. See issue #537 and <c>.design/design-migration.md</c>.
///
/// The migration works because <c>components.css</c> loads BEFORE the legacy
/// sheets, so a not-yet-migrated page's own rules still win and the new
/// component rules stay inert until the old ones are deleted. That is only true
/// <em>property by property</em>. A legacy rule overrides the properties it
/// names; every property the component rule sets that the legacy rule does
/// <em>not</em> name still applies. Where the two systems happen to reuse a
/// class name for different things, the leftovers land on the wrong element.
///
/// The live example: <c>.ra__menu</c> is the popup in the design system and the
/// <c>&lt;details&gt;</c> wrapper in this app. <c>tools.css</c> overrode
/// position and display, so the menu worked -- but <c>top: calc(100% + 4px)</c>
/// leaked onto a <c>position: relative</c> box and pushed every row-actions
/// kebab in the app down by its own height. It had been that way for months
/// because no screenshot in between happened to include a kebab.
///
/// So this is a test rather than a checklist: a screenshot only catches the
/// collisions on pages someone thought to look at, and the whole failure mode is
/// that nobody looks. The allow-list below shrinks as each component family
/// migrates and the legacy rules are deleted; a NEW entry means a class name
/// collided since the last run, and wants eyes on the page before it is added.
///
/// KNOWN BLIND SPOT, and it has already shipped a bug. This test asks only
/// "does the legacy rule fail to override a design property?" It never asks
/// "does the legacy rule override a design property with something
/// incompatible?" -- and the second question is the one that matters once a
/// MIGRATED page uses the class. <c>.form-grid</c> is the worked example: the
/// legacy rule names <c>display</c> and <c>gap</c>, so nothing leaks and this
/// test stays green, but it turns the design system's two-column grid into a
/// one-column flex stack on every page that has moved. Same for
/// <c>.field</c>'s <c>margin-bottom: 16px</c> and <c>.field__label</c>'s
/// uppercasing. All three are handled by the form-scaffolding bridge in
/// base.css rather than by this test. Widening it to flag differing values on
/// shared class names is issue #542.
/// </summary>
public sealed class ComponentCollisionTests
{
    private const string DesignSystemSheet = "components.css";

    /// <summary>
    /// Sheets that load after <see cref="DesignSystemSheet"/> and still carry
    /// pre-migration rules. Order matches the &lt;link&gt; order in App.razor.
    /// Delete an entry when its sheet retires.
    /// </summary>
    private static readonly string[] LegacySheets = ["base.css", "auth.css", "tools.css", "admin.css"];

    /// <summary>
    /// Properties that move or size a box. A leaked colour is a cosmetic
    /// surprise on one element; a leaked <c>top</c> moved every kebab in the
    /// app. Widen this if a leak of some other kind ever ships a real bug.
    /// </summary>
    private static readonly HashSet<string> LayoutProperties =
    [
        "position", "top", "right", "bottom", "left", "z-index", "float", "clear",
        "display", "flex", "flex-direction", "flex-wrap", "flex-grow", "flex-shrink",
        "flex-basis", "grid", "grid-template-columns", "grid-template-rows",
        "grid-template-areas", "grid-column", "grid-row", "grid-auto-flow",
        "align-items", "align-content", "align-self", "justify-content",
        "justify-items", "justify-self", "gap", "row-gap", "column-gap",
        "width", "min-width", "max-width", "height", "min-height", "max-height",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "margin-inline", "margin-block", "padding", "padding-top", "padding-right",
        "padding-bottom", "padding-left", "padding-inline", "padding-block",
        "overflow", "overflow-x", "overflow-y", "box-sizing", "transform",
        "border-radius", "border", "border-width", "inset",
    ];

    /// <summary>
    /// Collisions that have been looked at and are not worth fixing before the
    /// family they belong to migrates. Keyed "class|sheet". The reason is the
    /// point of the entry -- an unexplained line here is how the audit rots
    /// back into the checklist it replaced.
    /// </summary>
    private static readonly Dictionary<string, string> Accepted = new(StringComparer.Ordinal)
    {
        ["ra|tools.css"] =
            "Inert. .ra gains `position: relative`, but the popup is absolutely positioned inside " +
            ".ra__menu, which sets its own `position: relative` -- so the nearest positioned " +
            "ancestor is unchanged. Retires with the .ra* migration (#529).",

        ["ra__caret|tools.css"] =
            "The caret gains `width: var(--control-h)` over its natural padded width, and " +
            "`margin-left: -1px` on a box that already has `border-left: 0` -- so the pull closes " +
            "a hairline rather than overlapping a border. Retires with #529.",

        // module-card|base.css retired in the PR 8 audit. It was accepted as inert, and the
        // properties it named were -- but base.css also redefined `display`, `padding` and the
        // checked colour with legacy values, which this test does not look at (#542). Every page
        // applying it had already migrated, so the base.css copy went rather than being gated.
    };

    [Fact]
    public void No_unreviewed_layout_property_leaks_from_the_component_layer()
    {
        var wwwroot = FindWwwroot();
        var design = ParseBareClassRules(Path.Combine(wwwroot, DesignSystemSheet));

        var leaks = new List<string>();
        var accountedFor = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sheet in LegacySheets)
        {
            var legacy = ParseBareClassRules(Path.Combine(wwwroot, sheet));
            foreach (var (className, designProps) in design.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!legacy.TryGetValue(className, out var legacyProps)) continue;

                var leaked = designProps.Keys
                    .Where(p => LayoutProperties.Contains(p) && !IsOverridden(p, legacyProps))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (leaked.Count == 0) continue;

                var key = $"{className}|{sheet}";
                if (Accepted.ContainsKey(key))
                {
                    accountedFor.Add(key);
                    continue;
                }

                leaks.Add($".{className} (defined in both {DesignSystemSheet} and {sheet}) " +
                          $"leaks: {string.Join(", ", leaked)}");
            }
        }

        leaks.Should().BeEmpty(
            "a class defined in both {0} and a legacy sheet applies every property the legacy rule " +
            "does not name, on an element the design system never meant it for. Look at the class " +
            "on the page that uses it: either migrate it, rename the app's copy, or -- if it is " +
            "genuinely harmless -- add it to ComponentCollisionTests.Accepted with the reason. " +
            "See issue #537.{1}{1}{2}",
            DesignSystemSheet, Environment.NewLine, string.Join(Environment.NewLine, leaks));

        // The allow-list is supposed to shrink. A stale entry means a family
        // migrated and nobody deleted the note, which makes the next reader
        // trust a reason that no longer describes anything.
        var stale = Accepted.Keys.Except(accountedFor).OrderBy(k => k, StringComparer.Ordinal).ToList();
        stale.Should().BeEmpty(
            "these collisions no longer exist, so their entries in " +
            "ComponentCollisionTests.Accepted should be deleted: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// True when some legacy declaration overrides <paramref name="property"/>.
    /// Shorthands count: a legacy <c>padding: 10px 12px</c> does cancel a
    /// component <c>padding-right</c>. Comparing property names alone reports
    /// those as leaks, which is what put two false positives on #537's original
    /// list of six.
    /// </summary>
    private static bool IsOverridden(string property, IReadOnlyDictionary<string, string> legacy)
    {
        if (legacy.ContainsKey(property)) return true;

        foreach (var (shorthand, longhands) in Shorthands)
        {
            if (legacy.ContainsKey(shorthand) && longhands(property)) return true;
        }

        // grid-template-* only mean anything on a grid container, so a legacy
        // rule that makes the box a flex column has already neutralised them.
        if (property.StartsWith("grid-", StringComparison.Ordinal)
            && legacy.TryGetValue("display", out var display)
            && display is not ("grid" or "inline-grid"))
        {
            return true;
        }

        return false;
    }

    private static readonly (string Shorthand, Func<string, bool> Covers)[] Shorthands =
    [
        ("padding", p => p.StartsWith("padding-", StringComparison.Ordinal)),
        ("margin", p => p.StartsWith("margin-", StringComparison.Ordinal)),
        ("border", p => p.StartsWith("border-", StringComparison.Ordinal) && p != "border-radius"),
        ("border-radius", p => p.EndsWith("-radius", StringComparison.Ordinal)),
        ("inset", p => p is "top" or "right" or "bottom" or "left"),
        ("flex", p => p is "flex-grow" or "flex-shrink" or "flex-basis"),
        ("gap", p => p is "row-gap" or "column-gap"),
        ("overflow", p => p is "overflow-x" or "overflow-y"),
    ];

    /// <summary>
    /// Maps class name -> declared properties, for rules whose selector is a
    /// single bare class. Anything with a combinator, a second class, a pseudo
    /// or an element is a narrower rule and not the collision being hunted --
    /// those already win or lose on specificity rather than by accident.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> ParseBareClassRules(string path)
    {
        var text = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (Match rule in Regex.Matches(text, @"([^{}]+)\{([^{}]*)\}"))
        {
            var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var declaration in rule.Groups[2].Value.Split(';'))
            {
                var separator = declaration.IndexOf(':');
                if (separator < 0) continue;
                var name = declaration[..separator].Trim();
                // Custom properties are inherited values, not applied ones.
                if (name.Length == 0 || name.StartsWith("--", StringComparison.Ordinal)) continue;
                declarations[name] = declaration[(separator + 1)..].Trim();
            }

            foreach (var selector in rule.Groups[1].Value.Split(','))
            {
                var match = Regex.Match(selector.Trim(), @"^\.([A-Za-z0-9_-]+)$");
                if (!match.Success) continue;

                if (!rules.TryGetValue(match.Groups[1].Value, out var existing))
                {
                    rules[match.Groups[1].Value] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                foreach (var (name, value) in declarations) existing[name] = value;
            }
        }

        return rules;
    }

    /// <summary>
    /// Walks up from the test binary to the repo root marker, then down into the
    /// app's wwwroot. Mirrors <c>FontAssetTests.FindWwwroot</c>.
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
