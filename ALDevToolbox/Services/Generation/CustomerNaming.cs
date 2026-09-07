using System.Globalization;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Turns a free-text customer name into a machine name: the workspace folder,
/// the <c>.code-workspace</c> file name, the suggested repository name.
/// The single home for that act, so a customer typed once ("Jørgensen Møbler")
/// derives the same names everywhere. Specified in
/// <c>.design/customer-naming.md</c>.
/// </summary>
/// <remarks>
/// Transliteration runs first and is the same for every style: Unicode
/// canonical decomposition strips the combining marks (é to e, ä to a), and a
/// fixed table covers the letters that do not decompose (ø to o, å to aa, ß to
/// ss). It is deliberately not a setting - "å" becomes "aa" because that is the
/// Danish and Norwegian convention, and this tool's users are largely
/// Scandinavian. Whatever is still not a letter or digit afterwards is a word
/// separator, so word boundaries come from the typed name only: a run that is
/// already one word (CRONUS) keeps its own casing in PascalCase.
/// </remarks>
public static class CustomerNaming
{
    /// <summary>
    /// Longest result any style produces. Also GitHub's repository-name limit,
    /// which is the tightest consumer of these names.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// The longest short name the form accepts. Long customer names are what
    /// the short name exists to shorten, so the ceiling is well below the
    /// customer name's own.
    /// </summary>
    public const int MaxShortNameLength = 50;

    /// <summary>
    /// The longest extension name Business Central accepts. AppSourceCop
    /// AS0047 refuses anything longer; the platform stores the name as
    /// Text[250], so this is the safe ceiling for both cases.
    /// </summary>
    public const int MaxExtensionNameLength = 200;

    /// <summary>
    /// Letters that survive <see cref="NormalizationForm.FormD"/> intact
    /// because they have no canonical decomposition, and what each becomes.
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> Transliterations =
        new Dictionary<char, string>
        {
            ['æ'] = "ae", ['Æ'] = "AE",
            ['ø'] = "o", ['Ø'] = "O",
            ['å'] = "aa", ['Å'] = "AA",
            ['ß'] = "ss",
            ['œ'] = "oe", ['Œ'] = "OE",
            ['ð'] = "d", ['Ð'] = "D",
            ['đ'] = "d", ['Đ'] = "D",
            ['þ'] = "th", ['Þ'] = "TH",
            ['ł'] = "l", ['Ł'] = "L",
        };

    /// <summary>
    /// The machine name <paramref name="name"/> derives in
    /// <paramref name="style"/>, capped at <see cref="MaxLength"/> characters
    /// with any separator the cut left dangling trimmed off. A name with no
    /// letters or digits in it returns the empty string - callers validate with
    /// <see cref="HasNameCharacters"/> before they get here.
    /// </summary>
    public static string Apply(string? name, NamingStyle style)
    {
        var words = Words(name);
        if (words.Count == 0) return string.Empty;

        var joined = style switch
        {
            NamingStyle.PascalCase => string.Concat(words.Select(Capitalise)),
            NamingStyle.CamelCase => Decapitalise(Capitalise(words[0])) + string.Concat(words.Skip(1).Select(Capitalise)),
            NamingStyle.KebabCase => string.Join('-', words.Select(w => w.ToLowerInvariant())),
            NamingStyle.SnakeCase => string.Join('_', words.Select(w => w.ToLowerInvariant())),
            NamingStyle.Lowercase => string.Concat(words.Select(w => w.ToLowerInvariant())),
            NamingStyle.None => string.Join(' ', words),
            _ => string.Concat(words.Select(Capitalise)),
        };

        return joined.Length <= MaxLength ? joined : joined[..MaxLength].TrimEnd('-', '_', ' ');
    }

    /// <summary>
    /// The short name to render, or <paramref name="customerName"/> when no
    /// abbreviation was given. The short name is the consultant's own
    /// abbreviation rather than a derivation, so leaving it out is not a
    /// missing value - it means "the customer's name is short enough".
    /// See <c>.design/customer-naming.md</c>.
    /// </summary>
    public static string ShortNameOrFallback(string? shortName, string customerName) =>
        string.IsNullOrWhiteSpace(shortName) ? customerName : shortName.Trim();

    /// <summary>
    /// Whether <paramref name="name"/> still has at least one letter or digit
    /// once transliterated - i.e. whether it can name anything at all. "!!!"
    /// cannot; "Jørgensen Møbler" can.
    /// </summary>
    public static bool HasNameCharacters(string? name) => Words(name).Count > 0;

    /// <summary>
    /// Transliterates <paramref name="name"/> and splits it on everything that
    /// is not a letter or digit. Each word keeps the casing it was typed with.
    /// </summary>
    private static List<string> Words(string? name)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) return words;

        var word = new StringBuilder();
        foreach (var c in Transliterate(name))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// Applies the fixed table, then strips the combining marks canonical
    /// decomposition exposes. Composing first means a decomposed "å"
    /// (A + combining ring) is transliterated the same way a composed one is.
    /// </summary>
    private static string Transliterate(string name)
    {
        var composed = name.Normalize(NormalizationForm.FormC);
        var mapped = new StringBuilder(composed.Length);
        for (var i = 0; i < composed.Length; i++)
        {
            var c = composed[i];
            if (!Transliterations.TryGetValue(c, out var replacement))
            {
                mapped.Append(c);
                continue;
            }
            // An uppercase letter that expands to two ("Å" to "AA") only stays
            // shouted when the word around it is: "Åse" is "Aase", "ÅRHUS" is
            // "AARHUS". Judged by the letter that follows it, which is the only
            // thing that distinguishes the two.
            var followedByLowercase = i + 1 < composed.Length && char.IsLower(composed[i + 1]);
            mapped.Append(replacement.Length > 1 && char.IsUpper(c) && followedByLowercase
                ? replacement[0] + replacement[1..].ToLowerInvariant()
                : replacement);
        }

        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) stripped.Append(c);
        }
        return stripped.ToString();
    }

    /// <summary>Uppercases the first character and leaves the rest of the word alone, so "CRONUS" stays "CRONUS".</summary>
    private static string Capitalise(string word) =>
        char.ToUpperInvariant(word[0]) + word[1..];

    private static string Decapitalise(string word) =>
        char.ToLowerInvariant(word[0]) + word[1..];
}
