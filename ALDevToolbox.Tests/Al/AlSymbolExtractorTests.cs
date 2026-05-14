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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

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

        var symbols = AlSymbolExtractor.Extract(source);

        symbols.Should().ContainSingle();
        // "    procedure " is 14 characters; name starts at column 15.
        symbols[0].ColumnStart.Should().Be(15);
        symbols[0].ColumnEnd.Should().Be(15 + "DoTheThing".Length);
    }
}
