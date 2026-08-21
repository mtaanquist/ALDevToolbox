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
/// There are TWO halves to the hazard and a test for each, because the first
/// test alone was blind to the second and that blindness shipped bugs.
///
/// <list type="number">
/// <item><b>Leaks</b> -- the legacy rule fails to name a property the component
/// sets, so a component value lands on an element the design system never meant
/// it for. That is the <c>.ra__menu</c> kebab bug.</item>
/// <item><b>Overrides</b> -- the legacy rule names it with a DIFFERENT value.
/// Correct on an unmigrated page and a bug on a migrated one, where the page
/// asks for the component and silently gets the old thing. <c>.form-grid</c> is
/// the worked example: the legacy rule names <c>display</c> and <c>gap</c>, so
/// nothing leaks and the first test stays green, while the design system's
/// two-column grid renders as a one-column flex stack on every page that has
/// moved. The PR 8 audit found six more the same way -- <c>.card</c> on a heavy
/// drop shadow, every <c>.data-table</c> denser and smaller-headed than the
/// hand-off, <c>.audit</c> turned into a flex column by an unrelated component
/// of the same name. None of them looked broken, which is exactly the
/// problem.</item>
/// </list>
///
/// Both are cleared the same three ways: delete the legacy rule, restore the
/// component value under <c>.page</c> in the design-layer bridge, or add a
/// reasoned allow-list entry. Issues #537 and #542.
/// </summary>
public sealed class ComponentCollisionTests
{
    /// <summary>
    /// Every sheet in the design layer, in &lt;link&gt; order. All of them load
    /// before the legacy sheets, so all of them are exposed to the same hazard.
    ///
    /// This was <c>components.css</c> alone until PR 12, which is issue #557:
    /// the migration had moved on to porting pages onto <c>pages.css</c> /
    /// <c>pages-forms.css</c>, and the guard was still only watching the sheet
    /// the first few PRs used. It was hiding a live one — <c>.audit</c> is the
    /// audit-history panel in <c>pages-forms.css</c> and an unrelated key/value
    /// list in <c>tools.css</c>, the exact case the class doc-comment above
    /// names, and no test could see it because the design-side declaration had
    /// moved out of <c>components.css</c>.
    /// </summary>
    private static readonly string[] DesignSheets =
        ["components.css", "pages.css", "pages-forms.css", "pages-power.css",
         "pages-content.css"];

    /// <summary>
    /// Sheets that load after <see cref="DesignSystemSheet"/> and still carry
    /// pre-migration rules. Order matches the &lt;link&gt; order in App.razor.
    /// Delete an entry when its sheet retires.
    /// </summary>
    private static readonly string[] LegacySheets = ["base.css", "tools.css", "admin.css"];

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
        // ra|tools.css and ra__caret|tools.css retired in PR 15b: the whole legacy
        // .ra family went with #529, so there is nothing left in tools.css to collide.

        // module-card|base.css retired in the PR 8 audit. It was accepted as inert, and the
        // properties it named were -- but base.css also redefined `display`, `padding` and the
        // checked colour with legacy values, which the second test below now catches. Every page
        // applying it had already migrated, so the base.css copy went rather than being gated.
    };

    /// <summary>
    /// Shared class names where the legacy sheet deliberately sets a different
    /// value and no bridge entry is wanted. Keyed "class|sheet". Same discipline
    /// as <see cref="Accepted"/>: the reason is the entry's whole point, and the
    /// list is supposed to shrink.
    /// </summary>
    private static readonly Dictionary<string, string> AcceptedOverrides = new(StringComparer.Ordinal)
    {
        // ra__menu|tools.css and ra__caret|tools.css retired in PR 15b. `.ra__menu`
        // named two different things -- the popup in the design system, the <details>
        // wrapper here -- and that collision was the whole of #529. The legacy family
        // is deleted and every call site renders the system's markup, so the name means
        // one thing again. This was the live example in the class doc above.
    };

    [Fact]
    public void No_unreviewed_layout_property_leaks_from_the_component_layer()
    {
        var wwwroot = FindWwwroot();

        var leaks = new List<string>();
        var accountedFor = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sheet in LegacySheets)
        {
            var legacy = ParseBareClassRules(Path.Combine(wwwroot, sheet));
            foreach (var designSheet in DesignSheets)
            foreach (var (className, designProps) in ParseBareClassRules(Path.Combine(wwwroot, designSheet))
                         .OrderBy(p => p.Key, StringComparer.Ordinal))
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

                leaks.Add($".{className} (defined in both {designSheet} and {sheet}) " +
                          $"leaks: {string.Join(", ", leaked)}");
            }
        }

        leaks.Should().BeEmpty(
            "a class defined in both a design sheet ({0}) and a legacy sheet applies every property the legacy rule " +
            "does not name, on an element the design system never meant it for. Look at the class " +
            "on the page that uses it: either migrate it, rename the app's copy, or -- if it is " +
            "genuinely harmless -- add it to ComponentCollisionTests.Accepted with the reason. " +
            "See issue #537.{1}{1}{2}",
            string.Join(" / ", DesignSheets), Environment.NewLine, string.Join(Environment.NewLine, leaks));

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
    /// Properties beyond <see cref="LayoutProperties"/> that decide whether a
    /// component still reads as itself. A conflicting <c>display</c> breaks the
    /// layout loudly; a conflicting <c>font-size</c> or <c>text-transform</c>
    /// does not break anything and is exactly why the PR 8 audit found tables
    /// and labels quietly wearing the legacy look on migrated pages.
    ///
    /// Colour is deliberately absent. Every unmigrated page differs on colour by
    /// design, so including it would bury the signal.
    /// </summary>
    private static readonly HashSet<string> IdentityProperties =
    [
        "font-size", "font-weight", "letter-spacing", "text-transform", "text-align",
        "font-family", "box-shadow", "border-collapse", "border-spacing",
    ];

    /// <summary>
    /// The other half of the hazard, and the one that shipped bugs: the legacy
    /// rule DOES name the property, with a different value. On an unmigrated
    /// page that is correct — the old look is supposed to win until the family
    /// moves. On a MIGRATED page the component is asked for and the old one is
    /// silently served.
    ///
    /// A conflict is cleared one of three ways:
    /// <list type="number">
    /// <item>the design-layer bridge in <c>base.css</c> restores the component
    /// value under <c>.page</c>, which every migrated root carries and no legacy
    /// root does — that is what the bridge is for;</item>
    /// <item>an entry in <see cref="AcceptedOverrides"/> with a reason;</item>
    /// <item>the legacy rule is deleted, which is the real fix.</item>
    /// </list>
    /// Issue #542.
    /// </summary>
    [Fact]
    public void Migrated_pages_get_the_component_value_for_every_shared_class()
    {
        var wwwroot = FindWwwroot();
        var bridged = ParseBridgedPairs(Path.Combine(wwwroot, "base.css"));

        var conflicts = new List<string>();
        var accountedFor = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sheet in LegacySheets)
        {
            var legacy = ParseBareClassRules(Path.Combine(wwwroot, sheet));
            foreach (var designSheet in DesignSheets)
            foreach (var (className, designProps) in ParseBareClassRules(Path.Combine(wwwroot, designSheet))
                         .OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!legacy.TryGetValue(className, out var legacyProps)) continue;

                var clashing = designProps
                    .Where(p => LayoutProperties.Contains(p.Key) || IdentityProperties.Contains(p.Key))
                    .Where(p => legacyProps.TryGetValue(p.Key, out var theirs) && theirs != p.Value)
                    .Select(p => p.Key)
                    .Where(p => !bridged.Contains((className, p)))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (clashing.Count == 0) continue;

                var key = $"{className}|{sheet}";
                if (AcceptedOverrides.ContainsKey(key))
                {
                    accountedFor.Add(key);
                    continue;
                }

                conflicts.Add($".{className} ({sheet} overrides {designSheet}) " +
                              $"differs on: {string.Join(", ", clashing)}");
            }
        }

        conflicts.Should().BeEmpty(
            "a migrated page asking for one of these components silently gets the legacy one. " +
            "Fix it by deleting the legacy rule, or -- while the family still has unmigrated " +
            "callers -- by restoring the component value under `.page` in the design-layer " +
            "bridge in base.css. Only add to ComponentCollisionTests.AcceptedOverrides when the " +
            "difference is genuinely wanted. See issue #542.{0}{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, conflicts));

        var stale = AcceptedOverrides.Keys.Except(accountedFor).OrderBy(k => k, StringComparer.Ordinal).ToList();
        stale.Should().BeEmpty(
            "these overrides no longer conflict, so their entries in " +
            "ComponentCollisionTests.AcceptedOverrides should be deleted: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// (class, property) pairs the design-layer bridge already addresses — every
    /// rule in <c>base.css</c> whose selector is scoped to <c>.page</c>. Pairs
    /// rather than bare class names: the bridge restoring <c>.page .card</c>'s
    /// <c>border-radius</c> says nothing about its <c>box-shadow</c>.
    /// </summary>
    private static HashSet<(string Class, string Property)> ParseBridgedPairs(string path)
    {
        var text = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var pairs = new HashSet<(string, string)>();

        foreach (Match rule in Regex.Matches(text, @"([^{}]+)\{([^{}]*)\}"))
        {
            var properties = rule.Groups[2].Value.Split(';')
                .Select(d => d.IndexOf(':') is var i && i > 0 ? d[..i].Trim() : null)
                .Where(p => !string.IsNullOrEmpty(p) && !p!.StartsWith("--", StringComparison.Ordinal))
                .ToList();
            if (properties.Count == 0) continue;

            foreach (var selector in rule.Groups[1].Value.Split(','))
            {
                var trimmed = selector.Trim();
                if (!trimmed.StartsWith(".page ", StringComparison.Ordinal)) continue;
                foreach (Match cls in Regex.Matches(trimmed[".page ".Length..], @"\.([A-Za-z0-9_-]+)"))
                {
                    foreach (var property in properties) pairs.Add((cls.Groups[1].Value, property!));
                }
            }
        }

        return pairs;
    }

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
