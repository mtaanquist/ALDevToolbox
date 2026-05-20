using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Pins the symbol extractor used to populate <c>base_app_symbols</c> at
/// import time. Each test reads as a small AL fragment and asserts the
/// declaration rows the extractor should produce.
/// </summary>
public sealed class AlSymbolExtractorTests
{
    // Most tests below pre-date the field / object_declaration /
    // var_declaration extraction and only care about procedures,
    // triggers, and events. The helper hides those extras so the older
    // assertions stay focused; tests that exercise the new kinds call
    // the extractor directly.
    private static IReadOnlyList<AlSymbol> ExtractNonHeader(string source) =>
        AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind != "object_declaration" && s.Kind != "var_declaration")
            .ToList();

    [Fact]
    public void Extracts_public_procedure_with_signature()
    {
        var source = """
            codeunit 80 "Sales-Post"
            {
                procedure Post(var SalesHeader: Record "Sales Header"; Commit: Boolean)
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("procedure");
        symbols[0].Name.Should().Be("Post");
        symbols[0].Signature.Should().Be("(var SalesHeader: Record \"Sales Header\"; Commit: Boolean)");
        symbols[0].LineNumber.Should().Be(3);
        symbols[0].ColumnStart.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Classifies_scope_modifiers()
    {
        var source = """
            codeunit 1 "Foo"
            {
                procedure Pub()
                local procedure Loc()
                internal procedure Int()
                protected procedure Prot()
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().HaveCount(4);
        symbols.Select(s => s.Kind).Should().BeEquivalentTo(new[]
        {
            "procedure", "local_procedure", "internal_procedure", "protected_procedure"
        });
    }

    [Fact]
    public void Records_overloads_as_separate_rows()
    {
        var source = """
            codeunit 1 "Calc"
            {
                procedure Add(x: Integer): Integer
                begin
                end;

                procedure Add(x: Integer; y: Integer): Integer
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().HaveCount(2);
        symbols.Select(s => s.Name).Should().AllBe("Add");
        symbols.Select(s => s.LineNumber).Should().Equal(new[] { 3, 7 });
    }

    [Fact]
    public void Classifies_event_publisher_via_IntegrationEvent_attribute()
    {
        var source = """
            codeunit 80 "Sales-Post"
            {
                [IntegrationEvent(false, false)]
                local procedure OnAfterPostSalesDoc(var SalesHeader: Record "Sales Header")
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("event_publisher");
        symbols[0].Name.Should().Be("OnAfterPostSalesDoc");
    }

    [Fact]
    public void Classifies_event_publisher_via_BusinessEvent_attribute()
    {
        var source = """
            [BusinessEvent(true)]
            procedure OnCustomerCreated(var Customer: Record Customer)
            begin
            end;
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("event_publisher");
    }

    [Fact]
    public void Classifies_event_subscriber_via_EventSubscriber_attribute()
    {
        var source = """
            codeunit 50100 "Sales Subscriber"
            {
                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
                local procedure OnAfterPostSalesDocHandler(var SalesHeader: Record "Sales Header")
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("event_subscriber");
        symbols[0].Name.Should().Be("OnAfterPostSalesDocHandler");
    }

    [Fact]
    public void Handles_stacked_attributes_above_declaration()
    {
        var source = """
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnBeforePost', '', false, false)]
            [HandlerFunctions('ConfirmHandler')]
            local procedure SubHandler(var SalesHeader: Record "Sales Header")
            begin
            end;
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("event_subscriber");
    }

    [Fact]
    public void Picks_up_trigger_declarations()
    {
        var source = """
            codeunit 80 "Sales-Post"
            {
                trigger OnRun()
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("trigger");
        symbols[0].Name.Should().Be("OnRun");
    }

    [Fact]
    public void Ignores_declarations_inside_line_comments()
    {
        var source = """
            codeunit 1 "X"
            {
                // procedure FakeOne()
                procedure RealOne()
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Name.Should().Be("RealOne");
    }

    [Fact]
    public void Ignores_declarations_inside_block_comments()
    {
        var source = """
            codeunit 1 "X"
            {
                /* commented out:
                procedure Outdated()
                begin
                end;
                */
                procedure Current()
                begin
                end;
            }
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Name.Should().Be("Current");
    }

    [Fact]
    public void Returns_empty_for_files_without_declarations()
    {
        AlSymbolExtractor.Extract("// just a comment\n").Should().BeEmpty();
        AlSymbolExtractor.Extract("").Should().BeEmpty();
    }

    [Fact]
    public void Pending_event_marker_is_cleared_when_a_non_decl_line_intervenes()
    {
        // A bare attribute followed by something other than a procedure
        // shouldn't poison the next real declaration.
        var source = """
            [SomeRandomAttr]
            var
                X: Integer;
            procedure RegularOne()
            begin
            end;
            """;

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("procedure");
        symbols[0].Name.Should().Be("RegularOne");
    }

    [Fact]
    public void Captures_column_positions_for_click_target()
    {
        // Indented procedure declaration — ColumnStart should point at the
        // first character of the name token, not at the procedure keyword.
        var source = "    procedure DoTheThing()\n";

        var symbols = ExtractNonHeader(source);

        symbols.Should().ContainSingle();
        // "    procedure " is 14 characters; name starts at column 15.
        symbols[0].ColumnStart.Should().Be(15);
        symbols[0].ColumnEnd.Should().Be(15 + "DoTheThing".Length);
    }

    [Fact]
    public void Emits_object_declaration_row_for_table_header()
    {
        var source = """
            table 36 "Sales Header"
            {
            }
            """;

        var symbols = AlSymbolExtractor.Extract(source);

        symbols.Should().ContainSingle();
        symbols[0].Kind.Should().Be("object_declaration");
        symbols[0].Name.Should().Be("Sales Header");
        symbols[0].LineNumber.Should().Be(1);
        symbols[0].ColumnStart.Should().Be(10); // `table 36 ` is 9 chars; quote at col 10.
    }

    [Fact]
    public void Emits_object_declaration_row_for_codeunit_header()
    {
        var source = """
            codeunit 80 "Sales-Post"
            {
            }
            """;

        var headers = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "object_declaration")
            .ToList();

        headers.Should().ContainSingle();
        headers[0].Name.Should().Be("Sales-Post");
    }

    [Fact]
    public void Emits_object_declaration_row_for_unquoted_name()
    {
        // BC ships unquoted object names too (`table 5721 Purchasing`).
        var source = """
            table 5721 Purchasing
            {
            }
            """;

        var headers = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "object_declaration")
            .ToList();

        headers.Should().ContainSingle();
        headers[0].Name.Should().Be("Purchasing");
    }

    [Fact]
    public void Extracts_field_declarations()
    {
        var source = """
            table 36 "Sales Header"
            {
                fields
                {
                    field(1; "Document Type"; Enum "Sales Document Type")
                    {
                    }
                    field(2; "No."; Code[20])
                    {
                    }
                }
            }
            """;

        var fields = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field")
            .ToList();

        fields.Should().HaveCount(2);
        fields.Should().OnlyContain(f => f.Kind == "table_field",
            "the table-side (id; name; type) shape always emits table_field per "
            + ".design/al-reference-extractor-refactor.md step 1");
        fields[0].Name.Should().Be("Document Type");
        fields[0].FieldId.Should().Be(1);
        fields[0].Signature.Should().Be("Enum \"Sales Document Type\"");
        fields[1].Name.Should().Be("No.");
        fields[1].FieldId.Should().Be(2);
        fields[1].Signature.Should().Be("Code[20]");
    }

    [Fact]
    public void Extracts_page_field_declarations()
    {
        // Page / pageextension fields use a name+expression shape instead
        // of id+name+type. The extractor needs to pick these up so the
        // outline grouper can nest field-bound triggers (OnValidate,
        // OnLookup, OnAssistEdit, …) under their parent field.
        var source = """
            pageextension 50100 "Sales Header Ext" extends "Sales Header"
            {
                layout
                {
                    addafter("No.")
                    {
                        field("Sell-to Customer No."; Rec."Sell-to Customer No.")
                        {
                            trigger OnValidate()
                            begin
                            end;
                        }
                    }
                }
            }
            """;

        var fields = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field")
            .ToList();

        fields.Should().ContainSingle();
        fields[0].Kind.Should().Be("page_field",
            "the page-side (name; expr) shape always emits page_field per "
            + ".design/al-reference-extractor-refactor.md step 1");
        fields[0].Name.Should().Be("Sell-to Customer No.");
        fields[0].FieldId.Should().BeNull();
        fields[0].Signature.Should().Be("Rec.\"Sell-to Customer No.\"");
    }

    [Fact]
    public void Extracts_page_action_declarations()
    {
        // Action declarations anchor OnAction triggers in the outline,
        // the same way field declarations anchor OnValidate.
        var source = """
            page 50100 "My List"
            {
                actions
                {
                    area(processing)
                    {
                        action("Post")
                        {
                            trigger OnAction()
                            begin
                            end;
                        }
                    }
                }
            }
            """;

        var actions = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "page_action")
            .ToList();

        actions.Should().ContainSingle();
        actions[0].Name.Should().Be("Post");
    }

    [Fact]
    public void Extracts_query_column_declarations()
    {
        // `column(Name; "Source")` and `filter(Name; "Source")` inside
        // a query body become query_column symbols on the query type
        // so `MyQuery.Name` chains resolve through the catalog. Without
        // this, every access like
        // `CustLedgEntryRemainAmt.Sum_Remaining_Amt_LCY` strands as a
        // chain-step unresolved.
        var source = """
            query 21 "Cust. Ledg. Entry Remain. Amt."
            {
                elements
                {
                    dataitem(Cust_Ledger_Entry; "Cust. Ledger Entry")
                    {
                        filter(Document_Type; "Document Type") { }
                        column(Sum_Remaining_Amt_LCY; "Remaining Amt. (LCY)")
                        {
                            Method = Sum;
                        }
                    }
                }
            }
            """;

        var columns = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "query_column")
            .ToList();

        columns.Should().HaveCount(2);
        columns.Select(c => c.Name).Should().BeEquivalentTo(
            new[] { "Document_Type", "Sum_Remaining_Amt_LCY" });
        columns.Single(c => c.Name == "Sum_Remaining_Amt_LCY")
            .Signature.Should().Be("\"Remaining Amt. (LCY)\"");
    }

    [Fact]
    public void Query_column_does_not_fire_on_page_field_line()
    {
        // The query_column extraction is owner-kind gated so a page
        // body's `field(name; expr)` keeps emitting page_field, not a
        // hypothetical "column" mismatch.
        var source = """
            page 50100 "My List"
            {
                layout
                {
                    addafter("No.")
                    {
                        field("Sell-to Customer No."; Rec."Sell-to Customer No.") { }
                    }
                }
            }
            """;

        var symbols = AlSymbolExtractor.Extract(source);

        symbols.Should().NotContain(s => s.Kind == "query_column");
        symbols.Should().Contain(s => s.Kind == "page_field" && s.Name == "Sell-to Customer No.");
    }

    [Fact]
    public void Table_field_form_wins_over_page_field_form()
    {
        // The table-side regex requires id;name;type — strictly more
        // information than the page-side form. When a row matches the
        // table shape, we keep it as a table-style field (FieldId set).
        var source = """
                field(1; "No."; Code[20])
            """;

        var fields = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field")
            .ToList();

        fields.Should().ContainSingle();
        fields[0].FieldId.Should().Be(1);
        fields[0].Signature.Should().Be("Code[20]");
    }

    [Fact]
    public void Field_columns_point_at_the_name_token()
    {
        // The click affordance underlines the name, not the field keyword
        // or the ID number.
        var source = "    field(1; \"No.\"; Code[20])\n";

        var fields = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field")
            .ToList();

        fields.Should().ContainSingle();
        // "    field(1; " is 13 chars; quote of "No." sits at col 14.
        fields[0].ColumnStart.Should().Be(14);
        fields[0].ColumnEnd.Should().Be(14 + "\"No.\"".Length);
    }

    [Fact]
    public void Field_in_block_comment_is_ignored()
    {
        var source = """
            table 1 "X"
            {
                fields
                {
                    /* field(99; "Old"; Integer) */
                    field(1; "Real"; Integer)
                    {
                    }
                }
            }
            """;

        var fields = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field")
            .ToList();

        fields.Should().ContainSingle();
        fields[0].Name.Should().Be("Real");
    }

    // ── Variable declarations ─────────────────────────────────────

    [Fact]
    public void Extracts_object_scope_var_declaration_positions()
    {
        // Object-scope globals appear in oe_module_variables (typed from
        // the symbol package's binary metadata), but the package doesn't
        // carry source line/column. The extractor's var_declaration row
        // is what ReleaseImportService joins on to stamp positions for
        // Go-to-definition. See .design/al-reference-extractor-refactor.md
        // step 2.
        var source = """
            page 50100 "Sales Helper"
            {
                var
                    SalesDocCheckFactboxVisible: Boolean;
                    HelperCust: Record Customer;
            }
            """;

        var vars = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "var_declaration")
            .ToList();

        vars.Should().HaveCount(2);
        vars[0].Name.Should().Be("SalesDocCheckFactboxVisible");
        vars[0].LineNumber.Should().Be(4);
        vars[0].ColumnStart.Should().BeGreaterThan(1);
        vars[1].Name.Should().Be("HelperCust");
        vars[1].LineNumber.Should().Be(5);
    }

    [Fact]
    public void Label_declaration_wins_over_var_declaration()
    {
        // A Label-typed variable matches both regex shapes; the more
        // specific Label form runs first and the var path only emits
        // for non-Label declarations.
        var source = """
            codeunit 50100 "Foo"
            {
                var
                    UnsupportedTypeErr: Label 'Unsupported type %1.';
                    Counter: Integer;
            }
            """;

        var extracted = AlSymbolExtractor.Extract(source).ToList();

        extracted.Should().Contain(s => s.Kind == "label" && s.Name == "UnsupportedTypeErr");
        extracted.Should().Contain(s => s.Kind == "var_declaration" && s.Name == "Counter");
        extracted.Should().NotContain(s => s.Kind == "var_declaration" && s.Name == "UnsupportedTypeErr");
    }

    // ── Label declarations ─────────────────────────────────────────

    [Fact]
    public void Extracts_object_scope_label_declaration_with_content()
    {
        // The label content stashed in Signature is what makes the
        // customer-error-message search-and-navigate use case work —
        // the developer pastes the error text into search, lands here.
        var source = """
            codeunit 50100 "Foo"
            {
                var
                    UnsupportedTypeErr: Label 'Unsupported type %1.', Comment = '%1 is the type name';
            }
            """;

        var labels = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "label")
            .ToList();

        labels.Should().ContainSingle();
        labels[0].Name.Should().Be("UnsupportedTypeErr");
        labels[0].Signature.Should().Be("Unsupported type %1.");
    }

    [Fact]
    public void Extracts_procedure_local_label_declaration()
    {
        // Procedure-local labels are common when an error message is
        // only used in one procedure. AlSymbolExtractor is line-based
        // so it picks them up the same way as object-scope labels.
        var source = """
            codeunit 50100 "Foo"
            {
                procedure DoStuff()
                var
                    LocalErr: Label 'Local error %1.';
                begin
                end;
            }
            """;

        var labels = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "label")
            .ToList();

        labels.Should().ContainSingle();
        labels[0].Name.Should().Be("LocalErr");
        labels[0].Signature.Should().Be("Local error %1.");
    }

    [Fact]
    public void Label_with_doubled_apostrophe_unescapes_correctly()
    {
        // AL escapes single quotes as `''`. The content extractor must
        // un-double them so the stored signature matches what the user
        // would search for ("won't" not "won''t").
        var source = """
            codeunit 50100 "Foo"
            {
                var
                    Err: Label 'It''s broken.';
            }
            """;

        var labels = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "label")
            .ToList();

        labels.Should().ContainSingle();
        labels[0].Signature.Should().Be("It's broken.");
    }

    [Fact]
    public void Non_label_var_declarations_are_not_emitted_as_labels()
    {
        // Variables typed as anything other than Label must not get
        // label rows. The regex specifically requires `Label` after the
        // colon, so a Record / Integer / Code variable shouldn't match.
        var source = """
            codeunit 50100 "Foo"
            {
                var
                    Cust: Record Customer;
                    Counter: Integer;
                    Code: Code[20];
            }
            """;

        var labels = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "label")
            .ToList();

        labels.Should().BeEmpty();
    }

    // ── Page controls (issue #151 v2) ──────────────────────────────────

    [Fact]
    public void Extracts_page_group_as_page_control()
    {
        // A `group(General)` inside a page layout. Microsoft's XLIFF
        // LookupHint names this "Control General" — the extractor's
        // page_control row is what the translation resolver hits.
        var source = """
            page 50100 "My Card"
            {
                layout
                {
                    area(content)
                    {
                        group(General)
                        {
                            Caption = 'General';
                        }
                    }
                }
            }
            """;

        var controls = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "page_control")
            .ToList();

        controls.Should().ContainSingle();
        controls[0].Name.Should().Be("General");
        controls[0].LineNumber.Should().Be(7);
        controls[0].ColumnStart.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Extracts_page_repeater_cuegroup_and_part_as_page_control()
    {
        // All three keywords share the page-layout / page-control bucket
        // from a translation-resolution point of view — Microsoft emits
        // SubKind=control for any of them.
        var source = """
            page 50101 "My List"
            {
                layout
                {
                    area(content)
                    {
                        repeater(Group)
                        {
                            field("Code"; Rec.Code) { }
                        }
                        cuegroup(Activities)
                        {
                            Caption = 'Activities';
                        }
                        part(Lines; "My Subpage") { }
                    }
                }
            }
            """;

        var controls = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "page_control")
            .Select(s => s.Name)
            .ToList();

        controls.Should().BeEquivalentTo(new[] { "Group", "Activities", "Lines" });
    }

    [Fact]
    public void Does_not_emit_page_control_outside_page_object_kinds()
    {
        // `group(...)` inside a codeunit is — in practice — never the AL
        // page-layout keyword; but a defensive owner-kind gate keeps
        // hypothetical false matches out.
        var source = """
            codeunit 50100 "Sales-Post"
            {
                procedure Foo()
                begin
                end;
            }
            """;

        var controls = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "page_control")
            .ToList();

        controls.Should().BeEmpty();
    }

    [Fact]
    public void Page_control_quoted_names_are_unquoted_and_keep_column_position()
    {
        var source = """
            page 50102 "Quoted Names"
            {
                layout
                {
                    area(content)
                    {
                        group("General Info") { }
                    }
                }
            }
            """;

        var control = AlSymbolExtractor.Extract(source)
            .Single(s => s.Kind == "page_control");

        control.Name.Should().Be("General Info");
        control.ColumnStart.Should().BeGreaterThan(1);
        control.ColumnEnd.Should().Be(control.ColumnStart + "\"General Info\"".Length);
    }

    // ── Enum values (issue #151 v2) ────────────────────────────────────

    [Fact]
    public void Extracts_enum_values_with_id_and_name()
    {
        // The bare `value(N; Name)` form. FieldId carries the AL ordinal
        // so downstream consumers reuse the table_field id convention.
        var source = """
            enum 50100 "Document Status"
            {
                value(0; Open) { Caption = 'Open'; }
                value(1; Released) { Caption = 'Released'; }
                value(2; "Pending Approval") { Caption = 'Pending Approval'; }
            }
            """;

        var values = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "enum_value")
            .ToList();

        values.Should().HaveCount(3);
        values[0].Name.Should().Be("Open");
        values[0].FieldId.Should().Be(0);
        values[1].Name.Should().Be("Released");
        values[1].FieldId.Should().Be(1);
        values[2].Name.Should().Be("Pending Approval");
        values[2].FieldId.Should().Be(2);
    }

    [Fact]
    public void Does_not_emit_enum_value_outside_enum_object_kinds()
    {
        var source = """
            codeunit 50100 "Not An Enum"
            {
                procedure Foo()
                begin
                end;
            }
            """;

        var values = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "enum_value")
            .ToList();

        values.Should().BeEmpty();
    }

    [Fact]
    public void Extracts_enum_values_inside_enumextension()
    {
        var source = """
            enumextension 50100 "Status Ext" extends "Document Status"
            {
                value(10; Reopened) { Caption = 'Reopened'; }
            }
            """;

        var values = AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind == "enum_value")
            .ToList();

        values.Should().ContainSingle();
        values[0].Name.Should().Be("Reopened");
        values[0].FieldId.Should().Be(10);
    }
}
