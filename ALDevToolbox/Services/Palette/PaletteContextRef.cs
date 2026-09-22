using System.Globalization;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Palette;

/// <summary>The kinds of page that tell the palette what they are about.</summary>
public enum PaletteContextKind
{
    Solution,
    Environment,
}

/// <summary>
/// The one spelling of "this page is about that record" that a page hands the
/// shell and the shell hands back to <c>GET /palette/context</c>:
/// <c>solution:12</c>, <c>environment:7</c>. The page writes it (through
/// <c>Components/Shared/PaletteContext.razor</c>), the script carries it
/// verbatim, and only the server reads it - so the palette never parses a URL
/// and no page has to agree with the script about what its address means. See
/// <c>.design/command-palette.md</c>, "Where you are".
/// </summary>
public readonly partial record struct PaletteContextRef(PaletteContextKind Kind, int Id)
{
    /// <summary>What the page renders into <c>data-palette-context</c>.</summary>
    public override string ToString() =>
        $"{Kind.ToString().ToLowerInvariant()}:{Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The record's own page: the address a visit to it is remembered under, and
    /// the one the palette leaves out of Recent while you are standing on it.
    /// </summary>
    public string Href => Kind switch
    {
        PaletteContextKind.Solution => $"/solutions/{Id.ToString(CultureInfo.InvariantCulture)}",
        PaletteContextKind.Environment => $"/environments/{Id.ToString(CultureInfo.InvariantCulture)}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };

    /// <summary>
    /// Strict: a known kind, a colon, a positive id of at most nine digits, and
    /// nothing else. Anything the browser sends that is not exactly that is no
    /// context at all rather than a guess.
    /// </summary>
    public static bool TryParse(string? value, out PaletteContextRef result)
    {
        result = default;
        if (string.IsNullOrEmpty(value) || value.Length > 32) return false;

        var match = Shape().Match(value);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        var kind = match.Groups[1].Value == "solution"
            ? PaletteContextKind.Solution
            : PaletteContextKind.Environment;
        result = new PaletteContextRef(kind, id);
        return true;
    }

    [GeneratedRegex(@"^(solution|environment):([1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
