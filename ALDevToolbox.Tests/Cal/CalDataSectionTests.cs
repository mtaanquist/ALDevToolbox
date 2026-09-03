using System.Text;
using ALDevToolbox.Services.Cal;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Cal;

/// <summary>
/// Coverage for the report DATASET and xmlport ELEMENTS sections (issue #713):
/// their triggers are parsed like CODE-section bodies, each data item's
/// <c>DataItemTable</c> / element's <c>SourceTable</c> binds both a declarative
/// object reference and the implicit <c>Rec</c> its triggers run against, and a
/// column's <c>SourceExpr</c> is walked as code. Also pins the body-column
/// bookkeeping for code written on the <c>BEGIN</c> line.
/// </summary>
public sealed class CalDataSectionTests
{
    private static Encoding Cp850
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(850);
        }
    }

    internal static string FixturePath()
        => Path.Combine(AppContext.BaseDirectory, "Cal", "Fixtures", "CalDataSections.txt");

    private static CalParsedObject Parse(string typeKeyword)
    {
        using var fs = File.OpenRead(FixturePath());
        var block = CalObjectSplitter.Split(fs, Cp850).Single(b => b.TypeKeyword == typeKeyword);
        return CalObjectParser.Parse(block);
    }

    private static CalParsedObject Report() => Parse("Report");
    private static CalParsedObject XmlPort() => Parse("XMLport");

    [Fact]
    public void Parses_dataset_triggers_with_their_data_item_table()
    {
        var triggers = Report().Triggers;

        // Both data items' triggers, each bound to its own table.
        triggers.Should().Contain(t => t.Name == "OnPreDataItem" && t.RecTableId == 18);
        triggers.Should().Contain(t => t.Name == "OnAfterGetRecord" && t.RecTableId == 18);
        triggers.Should().Contain(t => t.Name == "OnAfterGetRecord" && t.RecTableId == 37);
        // The object-level trigger keeps no data-item binding.
        triggers.Should().Contain(t => t.Name == "OnPreReport" && t.RecTableId == null);
    }

    [Fact]
    public void Emits_data_item_tables_as_object_references()
    {
        Report().ObjectRefs.Should().BeEquivalentTo(new[]
        {
            new { Kind = "table", TargetId = 18 },
            new { Kind = "table", TargetId = 37 },
        }, o => o.ExcludingMissingMembers());

        XmlPort().ObjectRefs.Should().ContainSingle(r => r.Kind == "table" && r.TargetId == 18);
    }

    [Fact]
    public void Captures_column_source_expressions_with_the_owning_table()
    {
        var expressions = Report().Expressions;

        expressions.Should().Contain(e => e.Text == "\"Name\"" && e.RecTableId == 18);
        expressions.Should().Contain(e => e.Text == "SalesPost.CalcAmount(\"No.\")" && e.RecTableId == 18);
        // The nested data item's column inherits the nested table, not the parent's.
        expressions.Should().Contain(e => e.Text == "\"Line Amount\"" && e.RecTableId == 37);
    }

    [Fact]
    public void Parses_xmlport_element_triggers_with_their_source_table()
    {
        var triggers = XmlPort().Triggers;

        triggers.Should().Contain(t => t.Name == "OnAfterInitRecord" && t.RecTableId == 18);
        // A Field element inherits the enclosing element's table.
        triggers.Should().Contain(t => t.Name == "OnBeforePassField" && t.RecTableId == 18);
    }

    [Fact]
    public void Dataset_trigger_bodies_resolve_references_against_their_own_table()
    {
        var report = Report();
        var scope = ScopeFor(report, 18);
        var trigger = report.Triggers.Single(t => t.Name == "OnAfterGetRecord" && t.RecTableId == 18);

        var refs = CalReferenceExtractor.Extract(trigger.Body, scope).References;

        // CALCFIELDS("Balance") — the field argument lands on the data item's table.
        refs.Should().Contain(r => r.ReferenceKind == "field_access"
            && r.TargetKind == "table" && r.TargetId == 18 && r.MemberName == "Balance");
        refs.Should().OnlyContain(r => r.TargetId == 18);
    }

    [Fact]
    public void Column_source_expression_calling_a_codeunit_emits_a_method_call()
    {
        var report = Report();
        var expr = report.Expressions.Single(e => e.Text.StartsWith("SalesPost.", StringComparison.Ordinal));

        var refs = CalReferenceExtractor.Extract(expr.Text, ScopeFor(report, 18)).References;

        refs.Should().Contain(r => r.ReferenceKind == "method_call"
            && r.TargetKind == "codeunit" && r.TargetId == 80 && r.MemberName == "CalcAmount");
    }

    [Fact]
    public void Body_column_is_the_column_of_begin_so_same_line_code_stays_file_relative()
    {
        // "    OnPreReport=BEGIN SalesPost.CheckCustomer(''); END;"
        // BEGIN starts at column 17 and the called member at body column 17, so
        // the file column is 33 — the receiver "SalesPost" sits at 23.
        var trigger = Report().Triggers.Single(t => t.Name == "OnPreReport");
        trigger.BodyColumn.Should().Be(17);

        var call = CalReferenceExtractor.Extract(trigger.Body, ScopeFor(Report(), null)).References
            .Single(r => r.MemberName == "CheckCustomer");
        call.Line.Should().Be(1);
        (trigger.BodyColumn + call.Column - 1).Should().Be(33);
    }

    private static CalExtractScope ScopeFor(CalParsedObject parsed, int? recTableId)
    {
        var scope = new CalExtractScope
        {
            OwnerKind = parsed.Kind,
            OwnerId = parsed.ObjectId,
            Rec = recTableId is int id ? new CalTypeRef("table", id) : null,
        };
        foreach (var g in parsed.Globals)
        {
            if (g.TargetObjectId is int tid && g.TypeKeyword is not null
                && CalObjectKinds.ObjectTypeKeywordToKind.TryGetValue(g.TypeKeyword, out var k))
                scope.Variables[g.Name] = new CalTypeRef(k, tid);
        }
        return scope;
    }
}
