using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the one rule that keeps two form fields standing side by side level
/// with each other.
///
/// A <c>.field</c> is a grid of label, control and hint, and a <c>.form-grid</c>
/// puts two of them in one row. The row is as tall as the taller field, so the
/// shorter one is stretched - and a grid whose rows are all <c>auto</c> shares
/// that spare height out between its rows rather than leaving it at the end. The
/// label row grows, and the box under it drops below the box beside it: 18px on
/// New Workspace, where the left field's hint is one line shorter than the
/// right's (#813). Nothing in either field says so; the gap moves with whatever
/// the hints happen to say, which is why it reads as a mystery rather than a
/// rule.
///
/// <c>align-content: start</c> pins the rows to the top of whatever height the
/// field is given. It is written against <c>.form-grid .field</c> rather than
/// <c>.field</c> for two reasons: sharing a row with a taller field is the only
/// thing that stretches one, and a bare <c>.field</c> in app.css would be this
/// sheet redefining a component class, which is what
/// <see cref="ComponentCollisionTests"/> exists to catch.
///
/// Written against the rule, not the symptom: the symptom is a rendered pixel
/// count no unit test can see.
/// </summary>
public sealed class FieldRowAlignmentTests
{
    private const string App = "ALDevToolbox/wwwroot/app.css";

    [Fact]
    public void A_stretched_field_keeps_its_rows_at_the_top()
    {
        Rule(Read(App), ".form-grid .field").Should()
            .NotBeNull(because: $"{App} is where the app-level rule over the ported .field lives")
            .And.Contain("align-content: start",
                because: "without it a field stretched by a taller neighbour spreads the spare "
                       + "height between its own rows, pushing its label taller and its box "
                       + "below the one beside it (#813)");
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
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
