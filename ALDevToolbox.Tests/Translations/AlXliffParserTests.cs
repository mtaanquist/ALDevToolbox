using ALDevToolbox.Services.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.Translations;

/// <summary>
/// Tests the XLIFF v1.2 parser against the real-world FTU_Core.daDK.xlf
/// sample the user uploaded with the original issue. The parser is pure —
/// no DB, no DI — so these tests focus on shape (target language, trans-unit
/// count, kind bucketing) and on the developer-note → lookup-hint parser
/// that the import service relies on for symbol resolution.
/// </summary>
public sealed class AlXliffParserTests
{
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    [Fact]
    public void Parse_extracts_target_language_and_original_from_file_element()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        doc.TargetLanguage.Should().Be("da-DK");
        doc.OriginalName.Should().Be("FTU Core");
    }

    [Fact]
    public void Parse_walks_every_trans_unit_in_the_body()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        // FTU Core's da-DK XLIFF carries well over a hundred trans-units —
        // the exact count tracks Microsoft's build output and would be
        // brittle to pin, but the bare minimum sanity check ensures the
        // parser walked past the first <body> element.
        doc.Units.Should().HaveCountGreaterThan(100);
    }

    [Fact]
    public void Parse_carries_source_target_and_state_per_unit()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        var caption = doc.Units.Single(u => u.Id == "Table 792343850 - Field 1010272445 - Property 2879900210");
        caption.SourceText.Should().Be("Activate Assembly Order On Service");
        caption.TargetText.Should().Be("Aktivér montageordrer på service");
        caption.TargetState.Should().Be("translated");
    }

    [Fact]
    public void Parse_buckets_kinds_for_captions_tooltips_and_others()
    {
        using var stream = File.OpenRead(Path.Combine(FixtureRoot, "FTU_Core.daDK.xlf"));
        var doc = AlXliffParser.Parse(stream);

        var byKind = doc.Units
            .Select(u => AlXliffParser.BucketKind(u.Hint))
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());

        // The user said captions matter most, then tooltips — both buckets
        // should be present in any realistic Microsoft XLIFF.
        byKind.Should().ContainKey("caption").WhoseValue.Should().BeGreaterThan(0);
        byKind.Should().ContainKey("tooltip").WhoseValue.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ParseLookupHint_splits_table_field_property_into_segments()
    {
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
    public void ParseLookupHint_handles_top_level_property_without_sub_element()
    {
        var hint = AlXliffParser.ParseLookupHint("Table AppSetup - Property Caption");

        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("table");
        hint.ObjectName.Should().Be("AppSetup");
        hint.SubKind.Should().BeNull();
        hint.SubName.Should().BeNull();
        hint.PropertyName.Should().Be("Caption");
    }

    [Fact]
    public void ParseLookupHint_recognises_page_control_tooltip_shape()
    {
        var hint = AlXliffParser.ParseLookupHint(
            "PageExtension PaymentJournal - Control CheckTransmitted - Property ToolTip");

        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("pageextension");
        hint.ObjectName.Should().Be("PaymentJournal");
        hint.SubKind.Should().Be("control");
        hint.SubName.Should().Be("CheckTransmitted");
        hint.PropertyName.Should().Be("ToolTip");
    }

    [Fact]
    public void ParseLookupHint_recognises_named_type_label_shape()
    {
        var hint = AlXliffParser.ParseLookupHint("Codeunit Foo - NamedType ErrMsg");

        hint.Should().NotBeNull();
        hint!.ObjectKind.Should().Be("codeunit");
        hint.ObjectName.Should().Be("Foo");
        hint.SubKind.Should().Be("namedtype");
        hint.SubName.Should().Be("ErrMsg");
        hint.PropertyName.Should().BeNull();

        AlXliffParser.BucketKind(hint).Should().Be("label");
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
}
