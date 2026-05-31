using ALDevToolbox.Services.Cal;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Structural-parser coverage. Drives the same byte-preserved fixture the
/// splitter test uses and asserts the pieces the Object Explorer persists:
/// kind mapping, fields, globals with by-id targets, procedures, and triggers.
/// </summary>
public sealed class CalObjectParserTests
{
    private static CalParsedObject Parse(string typeKeyword)
    {
        var block = CalObjectSplitterTests.SplitFixture().Single(b => b.TypeKeyword == typeKeyword);
        return CalObjectParser.Parse(block);
    }

    private static CalParsedObject Customer() => Parse("Table");

    [Fact]
    public void Maps_object_type_to_kind()
    {
        Customer().Kind.Should().Be("table");
        Parse("Page").Kind.Should().Be("page");
        Parse("Query").Kind.Should().Be("query");
        Parse("XMLport").Kind.Should().Be("xmlport");
        Parse("MenuSuite").Kind.Should().Be("menusuite");
        Parse("Report").Kind.Should().Be("report");
    }

    [Fact]
    public void Parses_table_fields_with_id_name_and_type()
    {
        var fields = Customer().Fields;

        fields.Should().Contain(f => f.Id == 1 && f.Name == "No." && f.DataType == "Code20");
        fields.Should().Contain(f => f.Id == 2 && f.Name == "Name" && f.DataType == "Text100");
        fields.First(f => f.Id == 1).LineNumber.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Parses_object_globals_resolving_object_targets_by_id()
    {
        var globals = Customer().Globals;

        // SalesSetup@1002 : Record 311  and  NoSeriesMgt@... : Codeunit 396
        globals.Should().Contain(g =>
            g.Name == "SalesSetup" && g.TypeKeyword == "Record" && g.TargetObjectId == 311);
        globals.Should().Contain(g =>
            g.Name == "NoSeriesMgt" && g.TypeKeyword == "Codeunit" && g.TargetObjectId == 396);
        // A scalar global has no target.
        globals.Should().Contain(g => g.TypeKeyword == null && g.TargetObjectId == null);
    }

    [Fact]
    public void Parses_procedures_stripping_element_ids()
    {
        var procs = Customer().Procedures;

        var assistEdit = procs.Single(p => p.Name == "AssistEdit");
        assistEdit.IsLocal.Should().BeFalse();
        assistEdit.ReturnType.Should().Be("Boolean");
        assistEdit.Signature.Should().Be("(OldCust: Record 18)");   // @id stripped
        assistEdit.LineNumber.Should().BeGreaterThan(1);
        assistEdit.Body.Should().StartWith("BEGIN");

        procs.Should().Contain(p => p.Name == "GetTotalAmountLCYCommon" && p.IsLocal);
    }

    [Fact]
    public void Captures_table_triggers_from_properties_and_fields()
    {
        var triggers = Customer().Triggers.Select(t => t.Name).ToList();

        // Object-level triggers (PROPERTIES) and a field-level OnValidate.
        triggers.Should().Contain("OnInsert");
        triggers.Should().Contain("OnModify");
        triggers.Should().Contain("OnDelete");
        triggers.Should().Contain("OnValidate");
        Customer().Triggers.First(t => t.Name == "OnInsert").Body.Should().Contain("BEGIN");
    }

    [Fact]
    public void Parses_page_source_table_and_fields()
    {
        var page = Parse("Page");
        page.SourceTableId.Should().NotBeNull();
        page.PageFields.Should().NotBeEmpty();
    }
}
