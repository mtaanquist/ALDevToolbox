using ALDevToolbox.Services.Al;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// Pins the XML doc-comment capture the source extractor does for
/// <c>oe_module_symbols.doc</c> (issue #561).
///
/// Source is the only place a description exists — the AL compiler drops doc
/// comments, so no symbol package carries one. That makes these tests the
/// whole contract for the hover card's prose line, the outline tooltip, and
/// the <c>doc</c> field on <c>get_object_outline</c>.
/// </summary>
public sealed class AlSymbolDocTests
{
    private static string? DocFor(string source, string name) =>
        AlSymbolExtractor.Extract(source).Single(s => s.Name == name).Doc;

    [Fact]
    public void The_summary_above_a_procedure_becomes_its_doc()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>
                /// Posts the document and returns whether it was released.
                /// </summary>
                procedure PostDocument(DocumentNo: Code[20]): Boolean
                begin
                end;
            }
            """, "PostDocument");

        doc.Should().Be("Posts the document and returns whether it was released.");
    }

    /// <summary>
    /// A summary wrapped across several <c>///</c> lines is one sentence that
    /// happened to need wrapping. The card renders a single prose line, so the
    /// line breaks and the leading spaces have to be gone by the time it is
    /// stored — not left for three separate renderers to each strip.
    /// </summary>
    [Fact]
    public void A_wrapped_summary_is_flattened_to_one_line()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>
                /// After the calculation is done by calling ApplyPrice()
                /// the updated line is retrieved by this method.
                /// </summary>
                procedure GetLine(var Line: Variant)
                begin
                end;
            }
            """, "GetLine");

        doc.Should().Be(
            "After the calculation is done by calling ApplyPrice() the updated line is retrieved by this method.");
    }

    /// <summary>
    /// <c>&lt;param&gt;</c> and <c>&lt;returns&gt;</c> describe an argument
    /// and a result; neither says what the member does. The signature already
    /// names the parameters, so keeping them would put the same words on the
    /// card twice and push the sentence that matters out of view.
    /// </summary>
    [Fact]
    public void Param_and_returns_are_dropped()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>Returns the number of price list lines that fit the source line.</summary>
                /// <param name="ShowAll">If true it widens the filters set to the price list line.</param>
                /// <returns>Number of price list lines with discounts.</returns>
                procedure CountDiscount(ShowAll: Boolean) Result: Integer;
                begin
                end;
            }
            """, "CountDiscount");

        doc.Should().Be("Returns the number of price list lines that fit the source line.");
        doc.Should().NotContain("widens");
    }

    /// <summary>
    /// A block documenting only the parameters has said nothing about the
    /// member. Promoting the first <c>&lt;param&gt;</c> into the description
    /// slot would put "If true it widens the filters" on the card as though it
    /// described the procedure.
    /// </summary>
    [Fact]
    public void A_block_with_no_summary_yields_no_doc()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /// <param name="ShowAll">If true it widens the filters.</param>
                procedure PickDiscount(ShowAll: Boolean)
                begin
                end;
            }
            """, "PickDiscount");

        doc.Should().BeNull();
    }

    /// <summary>
    /// The tag-less form is what people actually type when they are not
    /// writing for a doc generator. It is still a description someone wrote by
    /// hand, and dropping it because the shape is irregular would lose the
    /// only documentation a lot of customer code has.
    /// </summary>
    [Fact]
    public void A_bare_doc_comment_with_no_tags_is_kept_whole()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /// Blocks the customer and writes the reason to the ledger.
                procedure BlockCustomer(Reason: Text[100])
                begin
                end;
            }
            """, "BlockCustomer");

        doc.Should().Be("Blocks the customer and writes the reason to the ledger.");
    }

    /// <summary>
    /// The failure this guards is the one that makes the feature actively
    /// misleading rather than merely absent: a doc block that survives a
    /// procedure body and staples itself onto the next declaration down the
    /// file. A wrong description reads exactly as confidently as a right one.
    /// </summary>
    [Fact]
    public void A_doc_block_does_not_drift_onto_the_next_procedure()
    {
        var source = """
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>Documents only the first one.</summary>
                procedure First()
                begin
                    Message('hello');
                end;

                procedure Second()
                begin
                end;
            }
            """;

        DocFor(source, "First").Should().Be("Documents only the first one.");
        DocFor(source, "Second").Should().BeNull();
    }

    /// <summary>
    /// Event publishers and subscribers carry their attribute between the doc
    /// block and the declaration, so the doc has to survive the attribute
    /// lines the same way the pending event marker does. These are also the
    /// declarations most worth documenting — an integration event's summary is
    /// often the only statement of when it fires.
    /// </summary>
    [Fact]
    public void A_doc_survives_the_attribute_lines_between_it_and_the_declaration()
    {
        var symbol = AlSymbolExtractor.Extract("""
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>Raised after the document is posted.</summary>
                [IntegrationEvent(false, false)]
                local procedure OnAfterPostDocument(DocumentNo: Code[20])
                begin
                end;
            }
            """).Single(s => s.Name == "OnAfterPostDocument");

        symbol.Kind.Should().Be("event_publisher");
        symbol.Doc.Should().Be("Raised after the document is posted.");
    }

    [Fact]
    public void A_table_field_carries_its_summary()
    {
        var doc = DocFor("""
            table 50100 "CRONUS Customer Block"
            {
                fields
                {
                    /// <summary>Why the customer was blocked.</summary>
                    field(2; "Block Reason"; Text[100]) { }
                }
            }
            """, "Block Reason");

        doc.Should().Be("Why the customer was blocked.");
    }

    /// <summary>
    /// A divider line of slashes matches "starts with three slashes" and is
    /// not a doc comment. Without the guard it arrives as a description made
    /// of punctuation, which is worse than no description at all.
    /// </summary>
    [Fact]
    public void A_slash_divider_is_not_a_doc_comment()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                ////////////////////////////////////////
                procedure Divided()
                begin
                end;
            }
            """, "Divided");

        doc.Should().BeNull();
    }

    /// <summary>
    /// Inside a block comment, <c>///</c> is prose about the code, not
    /// documentation of it — commonly a commented-out procedure that still
    /// carries the doc block it was written with.
    /// </summary>
    [Fact]
    public void A_triple_slash_inside_a_block_comment_is_not_a_doc_comment()
    {
        var doc = DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                /*
                /// <summary>Belongs to the procedure that was commented out.</summary>
                */
                procedure StillHere()
                begin
                end;
            }
            """, "StillHere");

        doc.Should().BeNull();
    }

    /// <summary>
    /// <c>cref</c> / <c>name</c> attributes hold the word the sentence needs,
    /// so stripping them as tags would delete it and leave "See  for details".
    /// </summary>
    [Theory]
    [InlineData("""Use <see cref="Codeunit 80"/> instead.""", "Use Codeunit 80 instead.")]
    [InlineData("""Validates <paramref name="LineNo"/> first.""", "Validates LineNo first.")]
    [InlineData("Fails when Qty &lt; 0 or Cost &amp; Price disagree.",
                "Fails when Qty < 0 or Cost & Price disagree.")]
    public void Inline_references_and_entities_survive_as_text(string summary, string expected)
    {
        var doc = DocFor($$"""
            codeunit 50100 "CRONUS Sample"
            {
                /// <summary>{{summary}}</summary>
                procedure Documented()
                begin
                end;
            }
            """, "Documented");

        doc.Should().Be(expected);
    }

    [Fact]
    public void An_undocumented_declaration_has_no_doc()
    {
        DocFor("""
            codeunit 50100 "CRONUS Sample"
            {
                procedure Plain()
                begin
                end;
            }
            """, "Plain").Should().BeNull();
    }

    /// <summary>
    /// The shape the whole feature was written for, taken verbatim from
    /// Microsoft's <c>PriceCalculation.Interface.al</c>: an interface whose
    /// members have no bodies at all, so every declaration is separated from
    /// the last only by its doc block. It is also the corpus where the payoff
    /// is largest — an interface is nothing but a list of names, and the
    /// summary is the only thing that says what each one is for.
    /// </summary>
    [Fact]
    public void Every_member_of_a_documented_interface_gets_its_own_summary()
    {
        var symbols = AlSymbolExtractor.Extract("""
            namespace Microsoft.Pricing.Calculation;

            using Microsoft.Pricing.PriceList;

            interface "Price Calculation"
            {
                /// <summary>
                /// Save the source line as an interface variable inside the price calculation codeunit
                /// </summary>
                /// <param name="LineWithPrice">The interface parameter for the document or journal line.</param>
                /// <returns>The updated source line.</returns>
                procedure Init(LineWithPrice: Interface "Line With Price"; PriceCalculationSetup: Record "Price Calculation Setup")

                /// <summary>
                /// Executes the calcluation of the discount amount.
                /// </summary>
                procedure ApplyDiscount()

                procedure Undocumented()
            }
            """);

        symbols.Single(s => s.Name == "Init").Doc.Should().Be(
            "Save the source line as an interface variable inside the price calculation codeunit");
        symbols.Single(s => s.Name == "ApplyDiscount").Doc.Should().Be(
            "Executes the calcluation of the discount amount.");
        symbols.Single(s => s.Name == "Undocumented").Doc.Should().BeNull();
    }
}
