using System.Xml;
using System.Xml.Linq;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One parsed XLIFF document (one <c>&lt;file&gt;</c> element). XLIFF v1.2,
/// the format the AL compiler emits under <c>Translations/</c>. We parse
/// just the bits the import service needs:
/// <list type="bullet">
///   <item><see cref="TargetLanguage"/> from <c>&lt;file target-language="..."&gt;</c></item>
///   <item><see cref="OriginalName"/> from <c>&lt;file original="..."&gt;</c> — match key against <c>Module.Name</c></item>
///   <item><see cref="Units"/>: each <c>&lt;trans-unit&gt;</c> as a <see cref="XliffTransUnit"/></item>
/// </list>
/// The "Xliff Generator" developer note is split into navigation hints
/// (object kind / name / sub-element / property) so the import service can
/// resolve them against <c>oe_module_symbols</c>.
/// </summary>
public sealed record XliffDocument(
    string TargetLanguage,
    string? SourceLanguage,
    string? OriginalName,
    IReadOnlyList<XliffTransUnit> Units);

/// <summary>
/// One <c>&lt;trans-unit&gt;</c>: id + source + target + state + developer note.
/// <see cref="Hint"/> is the parsed lookup hint when the developer note
/// matched the AL compiler's standard format; null when the note was empty
/// or shaped differently (older / hand-edited XLIFFs).
/// </summary>
public sealed record XliffTransUnit(
    string Id,
    string SourceText,
    string TargetText,
    string? TargetState,
    string? DeveloperNote,
    XliffLookupHint? Hint);

/// <summary>
/// Decomposition of the AL compiler's "Xliff Generator" note. Examples
/// from a real BC XLIFF:
/// <list type="bullet">
///   <item><c>Table AppSetup - Property Caption</c> → ObjectKind=table,
///     ObjectName=AppSetup, PropertyName=Caption</item>
///   <item><c>Table AppSetup - Field Activate Assembly On Service - Property Caption</c>
///     → adds SubKind=field, SubName="Activate Assembly On Service"</item>
///   <item><c>Page X - Control Y - Property ToolTip</c> → SubKind=control</item>
///   <item><c>Codeunit X - NamedType ErrMsg</c> → SubKind=namedtype,
///     SubName=ErrMsg, PropertyName=null (labels)</item>
/// </list>
/// </summary>
public sealed record XliffLookupHint(
    string ObjectKind,
    string? ObjectName,
    string? SubKind,
    string? SubName,
    string? PropertyName);

/// <summary>
/// Pure-function XLIFF v1.2 parser. No DB, no DI — feeds <see cref="TranslationImportService"/>.
///
/// Real-world XLIFFs from BC ship in (at least) two shapes:
/// <list type="bullet">
///   <item><b>Modern Microsoft (BC 20+)</b> — Developer note carries
///     <c>(LookupHint=Codeunit X - NamedType Y)</c>; the Xliff Generator
///     note is empty. Page-extension actions / controls have empty
///     notes entirely, and their structure lives in the trans-unit
///     <c>id</c> attribute plus an <c>ObjectTarget</c> note.</item>
///   <item><b>Older / hand-edited</b> — Xliff Generator note carries the
///     human-readable <c>"Table AppSetup - Field X - Property Caption"</c>
///     text and the Developer note is empty.</item>
/// </list>
/// The parser tries in this order: LookupHint regex against any note;
/// Xliff Generator note body; trans-unit <c>id</c> structural decode
/// against a small well-known-property-hash map. The last fallback gives
/// us ObjectKind / SubKind / PropertyName for navigation; the human-
/// readable ObjectName / SubName are null but the row still indexes for
/// substring search on the translated text.
/// </summary>
public static class AlXliffParser
{
    private static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:1.2";

    /// <summary>
    /// AL compiler's well-known property hash ids, harvested from the
    /// sample Microsoft XLIFFs in <c>Fixtures/ObjectExplorer/</c>. The map
    /// drives the trans-unit id structural fallback when neither note
    /// carries a textual hint. Unknown ids fall through as null — the row
    /// still lands; we just don't know which property bucket it belongs to.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WellKnownPropertyIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["2879900210"] = "Caption",
            ["1295455071"] = "ToolTip",
            ["62802879"]   = "OptionCaption",
        };

    // Matches `LookupHint=<content>)` embedded in a note body. AL identifiers
    // can't contain `)` so the non-greedy character-class stops at the
    // first closing paren, which is the literal end of the LookupHint
    // segment. The (Namespace=…) and (ObjectTarget=…) sibling segments
    // are separate parentheticals and don't interfere.
    private static readonly System.Text.RegularExpressions.Regex LookupHintRegex =
        new(@"LookupHint=([^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // First segment of a trans-unit id: "<Kind> <NumericHashId>". The kind
    // is one of the AL declaration kinds; the id is BC's hash — not the
    // human-readable AL object number — so we keep just the kind.
    private static readonly System.Text.RegularExpressions.Regex IdSegmentRegex =
        new(@"^(?<kind>[A-Za-z]+)\s+\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses an XLIFF stream. Throws <see cref="InvalidDataException"/>
    /// when the document doesn't carry the structure we expect (no
    /// <c>&lt;file&gt;</c>, no <c>target-language</c>, …) so callers can
    /// surface a field-keyed error to the operator. Malformed XML bubbles
    /// up as <see cref="XmlException"/>.
    /// </summary>
    public static XliffDocument Parse(Stream xliff)
    {
        ArgumentNullException.ThrowIfNull(xliff);

        var doc = XDocument.Load(xliff);
        var fileEl = doc.Root?.Element(Ns + "file")
            ?? throw new InvalidDataException("XLIFF document is missing the <file> element.");

        var targetLang = fileEl.Attribute("target-language")?.Value
            ?? throw new InvalidDataException("XLIFF <file> element is missing the target-language attribute.");
        var sourceLang = fileEl.Attribute("source-language")?.Value;
        var original = fileEl.Attribute("original")?.Value;

        var body = fileEl.Element(Ns + "body");
        var units = new List<XliffTransUnit>();
        if (body is not null)
        {
            foreach (var transUnit in body.Descendants(Ns + "trans-unit"))
            {
                var unit = ParseTransUnit(transUnit);
                if (unit is not null) units.Add(unit);
            }
        }

        return new XliffDocument(
            NormaliseLanguage(targetLang),
            sourceLang is null ? null : NormaliseLanguage(sourceLang),
            original,
            units);
    }

    private static XliffTransUnit? ParseTransUnit(XElement el)
    {
        var id = el.Attribute("id")?.Value;
        if (string.IsNullOrEmpty(id)) return null;

        var source = el.Element(Ns + "source")?.Value ?? string.Empty;
        var targetEl = el.Element(Ns + "target");
        var target = targetEl?.Value ?? string.Empty;
        var state = targetEl?.Attribute("state")?.Value;

        // Gather every note's text by its "from" attribute so the hint
        // resolver can prefer Developer (modern Microsoft format) over
        // Xliff Generator (older shape) without re-walking the children.
        string? developerNoteText = null;
        string? xliffGeneratorText = null;
        foreach (var note in el.Elements(Ns + "note"))
        {
            var from = note.Attribute("from")?.Value;
            var body = note.Value;
            if (string.IsNullOrWhiteSpace(body)) continue;
            if (string.Equals(from, "Developer", StringComparison.OrdinalIgnoreCase))
            {
                developerNoteText = body;
            }
            else if (string.Equals(from, "Xliff Generator", StringComparison.OrdinalIgnoreCase))
            {
                xliffGeneratorText = body;
            }
        }

        var hint = ResolveHint(id, developerNoteText, xliffGeneratorText);
        // Store whatever non-empty note we found so the row carries a
        // usable diagnostic blob — Developer takes precedence because
        // the modern Microsoft format puts the rich content there.
        var developerNote = developerNoteText ?? xliffGeneratorText;
        return new XliffTransUnit(id, source, target, state, developerNote, hint);
    }

    /// <summary>
    /// Three-way resolver for the lookup hint:
    /// <list type="number">
    ///   <item><c>(LookupHint=…)</c> embedded in any note body (modern
    ///     Microsoft format, both Developer and Xliff Generator are
    ///     considered)</item>
    ///   <item>The Xliff Generator note body parsed as a bare hint string
    ///     (older / FTU-style format)</item>
    ///   <item>Structural decode of the trans-unit <c>id</c> attribute,
    ///     mapped through <see cref="WellKnownPropertyIds"/> for the
    ///     property name (NamedType / Action / Control / Field /
    ///     Property segments are themselves the kinds)</item>
    /// </list>
    /// </summary>
    internal static XliffLookupHint? ResolveHint(string transUnitId, string? developerNote, string? xliffGeneratorNote)
    {
        // (1) LookupHint regex against either note.
        foreach (var src in new[] { developerNote, xliffGeneratorNote })
        {
            if (string.IsNullOrEmpty(src)) continue;
            var m = LookupHintRegex.Match(src);
            if (!m.Success) continue;
            var parsed = ParseLookupHint(m.Groups[1].Value);
            if (parsed is not null) return parsed;
        }

        // (2) Bare hint string in the Xliff Generator note body.
        if (!string.IsNullOrEmpty(xliffGeneratorNote))
        {
            var parsed = ParseLookupHint(xliffGeneratorNote);
            if (parsed is not null) return parsed;
        }

        // (3) Structural decode of the trans-unit id. Gives us
        // ObjectKind / SubKind / PropertyName but not the names.
        return ParseIdStructure(transUnitId);
    }

    /// <summary>
    /// Decodes the trans-unit <c>id</c> attribute as a structural
    /// fallback when neither note carries a hint. Examples from a real
    /// Microsoft OIOUBL XLIFF:
    /// <list type="bullet">
    ///   <item><c>Codeunit 1465371914 - NamedType 1138880009</c></item>
    ///   <item><c>PageExtension 1344584502 - Action 2103005105 - Property 1295455071</c></item>
    ///   <item><c>Table N - Field N - Property 2879900210</c></item>
    /// </list>
    /// Returns null when the id doesn't parse to a recognised shape.
    /// </summary>
    internal static XliffLookupHint? ParseIdStructure(string transUnitId)
    {
        var segments = transUnitId.Split(" - ", StringSplitOptions.None);
        if (segments.Length == 0) return null;

        var first = IdSegmentRegex.Match(segments[0]);
        if (!first.Success) return null;
        var objectKind = first.Groups["kind"].Value.ToLowerInvariant();

        string? subKind = null;
        string? propertyName = null;
        for (int i = 1; i < segments.Length; i++)
        {
            var m = IdSegmentRegex.Match(segments[i]);
            if (!m.Success) continue;
            var kind = m.Groups["kind"].Value;
            if (string.Equals(kind, "Property", StringComparison.OrdinalIgnoreCase))
            {
                var hash = segments[i].Substring(kind.Length).Trim();
                if (WellKnownPropertyIds.TryGetValue(hash, out var propName))
                {
                    propertyName = propName;
                }
            }
            else if (subKind is null)
            {
                subKind = kind.ToLowerInvariant();
            }
        }

        // Object name and sub name are intentionally null — the trans-unit
        // id only carries the hashed numeric ids, not the human-readable
        // names. The row still lands for substring search; the MCP tool
        // surfaces the kinds we did extract.
        return new XliffLookupHint(
            ObjectKind: objectKind,
            ObjectName: null,
            SubKind: subKind,
            SubName: null,
            PropertyName: propertyName);
    }

    /// <summary>
    /// Splits the developer note on " - " and walks the segments. First
    /// segment is "&lt;ObjectKind&gt; &lt;ObjectName&gt;"; subsequent
    /// segments are either "&lt;SubKind&gt; &lt;SubName&gt;" (Field /
    /// Control / Action / NamedType / Value / Method) or "Property
    /// &lt;PropertyName&gt;" (terminal). ObjectName and SubName may carry
    /// embedded spaces — segments are split on " - " (with surrounding
    /// spaces), so "Activate Assembly On Service" stays one segment.
    /// </summary>
    internal static XliffLookupHint? ParseLookupHint(string developerNote)
    {
        if (string.IsNullOrWhiteSpace(developerNote)) return null;

        var segments = developerNote.Split(" - ", StringSplitOptions.None);
        if (segments.Length == 0) return null;

        // First segment: "<Kind> <Name>". The kind is one word; name is the rest.
        var first = segments[0].Trim();
        var firstSpace = first.IndexOf(' ');
        if (firstSpace <= 0 || firstSpace >= first.Length - 1) return null;
        var objectKind = first.Substring(0, firstSpace).Trim();
        var objectName = first.Substring(firstSpace + 1).Trim();
        if (string.IsNullOrEmpty(objectKind) || string.IsNullOrEmpty(objectName)) return null;

        string? subKind = null;
        string? subName = null;
        string? propertyName = null;

        for (int i = 1; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            var sp = seg.IndexOf(' ');
            if (sp <= 0 || sp >= seg.Length - 1) continue;
            var head = seg.Substring(0, sp).Trim();
            var rest = seg.Substring(sp + 1).Trim();
            if (string.Equals(head, "Property", StringComparison.OrdinalIgnoreCase))
            {
                propertyName = rest;
            }
            else
            {
                // Field / Control / Action / NamedType / Value / Method / etc.
                // Only the first sub-element wins; the AL compiler doesn't
                // emit chains beyond one level on shapes we've seen.
                if (subKind is null)
                {
                    subKind = head;
                    subName = rest;
                }
            }
        }

        return new XliffLookupHint(
            ObjectKind: objectKind.ToLowerInvariant(),
            ObjectName: objectName,
            SubKind: subKind?.ToLowerInvariant(),
            SubName: subName,
            PropertyName: propertyName);
    }

    /// <summary>
    /// Buckets the AL property name into the coarse <see cref="ModuleTranslation"/>
    /// kind enum so the MCP tool's kind filter has a small vocabulary.
    /// Trans-units without a hint, or with neither a property nor a
    /// recognised sub-kind, fall through as <c>other</c>.
    /// </summary>
    public static string BucketKind(XliffLookupHint? hint)
    {
        if (hint is null) return "other";
        if (!string.IsNullOrEmpty(hint.PropertyName))
        {
            return hint.PropertyName.ToLowerInvariant() switch
            {
                "caption" or "captionml" => "caption",
                "tooltip" or "tooltipml" => "tooltip",
                "instructionaltext" or "instructionaltextml" => "instructional",
                "optioncaption" or "optioncaptionml" => "option",
                _ => "other",
            };
        }
        if (string.Equals(hint.SubKind, "namedtype", StringComparison.OrdinalIgnoreCase))
        {
            return "label";
        }
        return "other";
    }

    /// <summary>
    /// Normalises a target-language attribute to BCP-47 <c>xx-XX</c> shape.
    /// Microsoft ships them as <c>da-DK</c> already; some hand-edited
    /// XLIFFs use <c>da_DK</c> or all-lowercase forms.
    /// </summary>
    private static string NormaliseLanguage(string raw)
    {
        var s = raw.Trim().Replace('_', '-');
        var dash = s.IndexOf('-');
        if (dash <= 0 || dash >= s.Length - 1) return s.ToLowerInvariant();
        var lang = s.Substring(0, dash).ToLowerInvariant();
        var region = s.Substring(dash + 1).ToUpperInvariant();
        return $"{lang}-{region}";
    }
}
