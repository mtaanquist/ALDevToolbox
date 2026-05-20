using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.Translations;

/// <summary>
/// Tests the XLIFF v1.2 parser against a real Microsoft OIOUBL <c>.xlf</c>
/// sample committed under <c>Fixtures/ObjectExplorer/</c>. The OIOUBL
/// shipping format puts the lookup hint inside the Developer note as
/// <c>(LookupHint=Codeunit X - NamedType Y)</c> with empty Xliff
/// Generator notes, plus a separate <c>ObjectTarget</c> note carrying the
/// base object's hashed id — that's the canonical Microsoft shape per
/// GitHub issue #151. The parser is pure (no DB, no DI); these tests
/// pin both the LookupHint extraction and the trans-unit id structural
/// decode that catches kinds we still want to bucket when neither note
/// carries a textual hint (page controls, page actions).
/// </summary>
public sealed class AlXliffParserTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    [Fact]
    public void Parse_extracts_target_language_and_original_from_file_element()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        doc.TargetLanguage.Should().Be("da-DK");
        doc.SourceLanguage.Should().Be("en-US",
            because: "the importer's source==target skip uses this to drop AL .g.xlf generator templates");
        doc.OriginalName.Should().Be("OIOUBL");
    }

    [Fact]
    public void Parse_walks_every_trans_unit_in_the_body()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        // OIOUBL's da-DK XLIFF carries 436 trans-units. Pinned because the
        // file is committed verbatim — a re-count failure points at a
        // parser regression, not at upstream content drift.
        doc.Units.Should().HaveCount(436);
    }

    [Fact]
    public void Parse_carries_source_target_and_state_per_unit()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        var namedTypeRow = doc.Units.Single(u => u.Id == "Codeunit 1465371914 - NamedType 1138880009");
        namedTypeRow.SourceText.Should().Be("The total Line Discount Amount cannot be negative.");
        namedTypeRow.TargetText.Should().Be("Det samlede linjerabatbeløb må ikke være negativt.");
        namedTypeRow.TargetState.Should().Be("translated");
    }

    [Fact]
    public void Parse_extracts_lookup_hint_from_developer_note_modern_microsoft_format()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        var row = doc.Units.Single(u => u.Id == "Codeunit 1465371914 - NamedType 1138880009");
        row.Hint.Should().NotBeNull();
        row.Hint!.ObjectKind.Should().Be("codeunit");
        row.Hint.ObjectName.Should().Be("OIOUBL-Check Sales Header");
        row.Hint.SubKind.Should().Be("namedtype");
        row.Hint.SubName.Should().Be("DiscountAmountNegativeErr");
        row.Hint.PropertyName.Should().BeNull();
        AlXliffParser.BucketKind(row.Hint).Should().Be("label");
    }

    [Fact]
    public void Parse_buckets_kinds_for_captions_tooltips_and_labels()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        var byKind = doc.Units
            .Select(u => AlXliffParser.BucketKind(u.Hint))
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());

        // OIOUBL ships error messages (labels), field captions, and
        // tooltips. All three buckets should be present — the user said
        // captions + labels are the priority and tooltips are opt-in,
        // so the bucketing has to distinguish them at parse time.
        byKind.Should().ContainKey("caption").WhoseValue.Should().BeGreaterThan(0);
        byKind.Should().ContainKey("tooltip").WhoseValue.Should().BeGreaterThan(0);
        byKind.Should().ContainKey("label").WhoseValue.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_falls_back_to_trans_unit_id_when_notes_are_empty()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        // PageExtension Action / Property trans-units in modern Microsoft
        // XLIFFs ship with both notes empty; the structural decode of the
        // trans-unit id is the only signal we have for those kinds.
        var pageExtAction = doc.Units.First(u => u.Id.StartsWith("PageExtension ") && u.Id.Contains("Action"));
        pageExtAction.Hint.Should().NotBeNull();
        pageExtAction.Hint!.ObjectKind.Should().Be("pageextension");
        pageExtAction.Hint.SubKind.Should().Be("action");
        pageExtAction.Hint.ObjectName.Should().BeNull(
            because: "the trans-unit id only carries hashed numeric ids — the human name isn't recoverable without the LookupHint note");
    }

    [Fact]
    public void Parse_resolves_property_id_2879900210_to_caption_kind()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        // 2879900210 is BC's well-known property hash for Caption; the
        // structural decode bucketing must light up even when the notes
        // are empty.
        var captionRow = doc.Units.First(u => u.Id.EndsWith("- Property 2879900210"));
        AlXliffParser.BucketKind(captionRow.Hint).Should().Be("caption");
    }

    [Fact]
    public void ParseLookupHint_splits_kind_name_field_caption_into_segments()
    {
        // Bare hint string (the older / FTU-style shape).
        var hint = AlXliffParser.ParseLookupHint(
            "Table AppSetup - Field Activate Assembly On Service - Property Caption");

        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("table");
        hint.ObjectName.Should().Be("AppSetup");
        hint.SubKind.Should().Be("field");
        hint.SubName.Should().Be("Activate Assembly On Service");
        hint.PropertyName.Should().Be("Caption");
    }

    [Fact]
    public void ResolveHint_extracts_lookup_hint_with_surrounding_parentheticals()
    {
        // The Microsoft developer note shape: leading comment + adjacent
        // (Namespace=…) and (ObjectTarget=…) segments around the
        // (LookupHint=…) one. The regex has to scope to LookupHint
        // without sweeping the namespace name into the match.
        var note =
            "%1 - document type, %2 - document no.(Namespace=Microsoft.EServices.EDocument)"
            + "(LookupHint=Codeunit OIOUBL-Check Sales Header - NamedType EmptyDescriptionErr)";

        var hint = AlXliffParser.ResolveHint("Codeunit 1 - NamedType 2", note, null);
        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("codeunit");
        hint.ObjectName.Should().Be("OIOUBL-Check Sales Header");
        hint.SubKind.Should().Be("namedtype");
        hint.SubName.Should().Be("EmptyDescriptionErr");
    }

    [Fact]
    public void ParseIdStructure_decodes_table_field_caption_shape_without_names()
    {
        var hint = AlXliffParser.ParseIdStructure(
            "Table 808645663 - Field 2273653569 - Property 2879900210");

        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("table");
        hint.SubKind.Should().Be("field");
        hint.PropertyName.Should().Be("Caption");
        hint.ObjectName.Should().BeNull();
        hint.SubName.Should().BeNull();
    }

    [Fact]
    public void Parse_throws_on_missing_target_language_attribute()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
                <file source-language="en-US" original="Foo"><body /></file>
            </xliff>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        Action act = () => AlXliffParser.Parse(stream);
        act.Should().Throw<InvalidDataException>().WithMessage("*target-language*");
    }

    // ── Streaming parser (issue #207) ──────────────────────────────────

    [Fact]
    public void ParseStreaming_yields_each_trans_unit_once_in_document_order()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
        var ids = new List<string>();

        var header = AlXliffParser.ParseStreaming(stream, unit => ids.Add(unit.Id));

        header.TargetLanguage.Should().Be("da-DK");
        ids.Should().HaveCount(436,
            because: "the streaming walker must yield exactly the same set of trans-units as the materialising Parse");
        ids.Distinct().Should().HaveCount(436,
            because: "no trans-unit is yielded twice — ReadSubtree advances past the end of each <trans-unit> element");
    }

    [Fact]
    public void ParseStreaming_emits_same_header_as_Parse()
    {
        using (var s = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf")))
        {
            var doc = AlXliffParser.Parse(s);

            using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
            var header = AlXliffParser.ParseStreaming(s2, _ => { });

            header.TargetLanguage.Should().Be(doc.TargetLanguage);
            header.SourceLanguage.Should().Be(doc.SourceLanguage);
            header.OriginalName.Should().Be(doc.OriginalName);
        }
    }

    [Fact]
    public void ParseStreaming_handles_trans_units_without_target_element()
    {
        // Legacy / hand-edited XLIFFs sometimes ship <trans-unit> without
        // a <target> child. The streaming walker should treat the target
        // text as empty rather than skip the row.
        const string xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
                <file source-language="en-US" target-language="da-DK" original="Foo">
                    <body>
                        <trans-unit id="t1">
                            <source>Hello</source>
                        </trans-unit>
                    </body>
                </file>
            </xliff>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var units = new List<XliffTransUnit>();
        var header = AlXliffParser.ParseStreaming(stream, units.Add);

        header.TargetLanguage.Should().Be("da-DK");
        units.Should().ContainSingle();
        units[0].Id.Should().Be("t1");
        units[0].SourceText.Should().Be("Hello");
        units[0].TargetText.Should().BeEmpty();
        units[0].TargetState.Should().BeNull();
    }

    [Fact]
    public void ParseStreaming_throws_on_missing_file_element()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2" />
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        Action act = () => AlXliffParser.ParseStreaming(stream, _ => { });
        act.Should().Throw<InvalidDataException>().WithMessage("*<file>*");
    }
}
