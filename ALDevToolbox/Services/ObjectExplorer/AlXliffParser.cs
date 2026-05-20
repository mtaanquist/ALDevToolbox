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
    string ObjectName,
    string? SubKind,
    string? SubName,
    string? PropertyName);

/// <summary>
/// Pure-function XLIFF v1.2 parser. No DB, no DI — feeds <see cref="TranslationImportService"/>.
/// </summary>
public static class AlXliffParser
{
    private static readonly XNamespace Ns = "urn:oasis:names:tc:xliff:document:1.2";

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

        return new XliffDocument(NormaliseLanguage(targetLang), original, units);
    }

    private static XliffTransUnit? ParseTransUnit(XElement el)
    {
        var id = el.Attribute("id")?.Value;
        if (string.IsNullOrEmpty(id)) return null;

        var source = el.Element(Ns + "source")?.Value ?? string.Empty;
        var targetEl = el.Element(Ns + "target");
        // Trans-units that haven't been translated yet still come through
        // as rows with empty target text — they're useful as search misses
        // ("the developer hasn't translated this caption yet") and as a
        // canvas the structured admin tools could light up later. Skipping
        // here would lose that signal.
        var target = targetEl?.Value ?? string.Empty;
        var state = targetEl?.Attribute("state")?.Value;

        string? developerNote = null;
        foreach (var note in el.Elements(Ns + "note"))
        {
            var from = note.Attribute("from")?.Value;
            // The AL compiler emits two notes: an empty "Developer" one and
            // the structured "Xliff Generator" one ("Table AppSetup - Field
            // X - Property Caption"). Some older / hand-edited XLIFFs flip
            // the convention and stuff the structured info into the
            // Developer note. Prefer Xliff Generator when both exist; fall
            // back to Developer otherwise.
            if (string.Equals(from, "Xliff Generator", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(note.Value))
                {
                    developerNote = note.Value;
                    break;
                }
            }
            else if (developerNote is null && !string.IsNullOrWhiteSpace(note.Value))
            {
                developerNote = note.Value;
            }
        }

        var hint = developerNote is null ? null : ParseLookupHint(developerNote);
        return new XliffTransUnit(id, source, target, state, developerNote, hint);
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
