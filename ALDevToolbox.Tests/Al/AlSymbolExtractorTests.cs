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
    // Most tests below pre-date the field / object_declaration extraction
    // and only care about procedures, triggers, and events. The helper
    // hides the object header row so those assertions stay focused; tests
    // that exercise the new kinds call the extractor directly.
    private static IReadOnlyList<AlSymbol> ExtractNonHeader(string source) =>
        AlSymbolExtractor.Extract(source)
            .Where(s => s.Kind != "object_declaration")
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
            .Where(s => s.Kind == "field")
            .ToList();

        fields.Should().HaveCount(2);
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
            .Where(s => s.Kind == "field")
            .ToList();

        fields.Should().ContainSingle();
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
            .Where(s => s.Kind == "action")
            .ToList();

        actions.Should().ContainSingle();
        actions[0].Name.Should().Be("Post");
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
            .Where(s => s.Kind == "field")
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
            .Where(s => s.Kind == "field")
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
            .Where(s => s.Kind == "field")
            .ToList();

        fields.Should().ContainSingle();
        fields[0].Name.Should().Be("Real");
    }
}
