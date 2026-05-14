using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="AlResolvableTokenScanner"/>. Pins the
/// resolution rules: <em>symbol names</em> resolve only at call sites
/// (token followed by <c>(</c>), <em>object names</em> only resolve in a
/// keyword-preceded context (<c>Record "Sales Header"</c>,
/// <c>Codeunit::"Sales-Post"</c>). Lines starting with <c>using</c> /
/// <c>namespace</c> are skipped entirely.
/// </summary>
public sealed class AlResolvableTokenScannerTests
{
    private static ResolvableVocabulary Symbols(params string[] names) =>
        new(ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(names, StringComparer.OrdinalIgnoreCase),
            FieldNamesInThisFile: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldsByVariable: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

    private static ResolvableVocabulary Objects(params string[] names) =>
        new(ObjectNames: new HashSet<string>(names, StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldNamesInThisFile: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldsByVariable: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

    private static ResolvableVocabulary FieldsInFile(params string[] names) =>
        new(ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldNamesInThisFile: new HashSet<string>(names, StringComparer.OrdinalIgnoreCase),
            FieldsByVariable: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

    private static ResolvableVocabulary FieldsByVar(string varName, params string[] fieldNames) =>
        new(ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldNamesInThisFile: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldsByVariable: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [varName] = new HashSet<string>(fieldNames, StringComparer.OrdinalIgnoreCase),
            });

    [Fact]
    public void Symbol_name_resolves_in_bare_call_site()
    {
        var source = "    Post(Header, true);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(1);
        ranges[0].ColumnStart.Should().Be(5);
        ranges[0].ColumnEnd.Should().Be(5 + "Post".Length);
    }

    [Fact]
    public void Object_name_resolves_after_Record_keyword()
    {
        var source = "    SalesHeader: Record \"Sales Header\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Sales Header"));

        ranges.Should().ContainSingle();
        ranges[0].ColumnStart.Should().Be(source.IndexOf('"') + 1);
        ranges[0].ColumnEnd.Should().Be(source.LastIndexOf('"') + 2);
    }

    [Fact]
    public void Object_name_resolves_after_Codeunit_keyword()
    {
        var source = "    Cu: Codeunit \"Sales-Post\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Sales-Post"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Object_name_resolves_after_double_colon_operator()
    {
        var source = "    if X = Codeunit::\"Sales-Post\" then exit;\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Sales-Post"));

        ranges.Should().ContainSingle();
    }

    [Theory]
    [InlineData("page")]
    [InlineData("report")]
    [InlineData("query")]
    [InlineData("xmlport")]
    [InlineData("enum")]
    [InlineData("interface")]
    [InlineData("testpage")]
    [InlineData("testpart")]
    [InlineData("testrequestpage")]
    [InlineData("requestpage")]
    [InlineData("permissionset")]
    [InlineData("profile")]
    [InlineData("controladdin")]
    public void Object_name_resolves_after_each_recognised_keyword(string keyword)
    {
        var source = $"    X: {keyword} \"Some Object\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Some Object"));

        ranges.Should().ContainSingle($"keyword '{keyword}' should set object-reference context");
    }

    [Fact]
    public void Object_name_resolves_after_extends_keyword()
    {
        // pageextension declarations name their base via `extends "Base"` —
        // clicking the base name should jump to the underlying object.
        var source = "pageextension 50100 \"My Ext\" extends \"Customer Card\"\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Customer Card"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Object_name_does_NOT_resolve_after_primitive_type_keyword()
    {
        // `Text` is a primitive type and must not act as object-reference
        // context — otherwise `Msg: Text "Label"` would underline "Label".
        var source = "    Msg: Text \"Label\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Label"));

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void Object_name_does_NOT_resolve_without_keyword_context()
    {
        // `Item` is the name of a table but also a common variable name —
        // without a preceding keyword, it should not be underlined.
        var source = "    Item.SetRange(\"No.\", 'X');\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Item"));

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void Object_keyword_check_skips_whitespace_between_keyword_and_name()
    {
        var source = "    X: Record    \"Sales Header\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Sales Header"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Same_name_in_both_buckets_resolves_either_way()
    {
        // `Item` is both an object (table) and a symbol (procedure). The
        // call site `Item(` resolves via the symbol rule, the keyword-
        // preceded `Codeunit Item` resolves via the object rule.
        var source = "    Item(rec); Cu: Codeunit Item;\n";
        var vocab = new ResolvableVocabulary(
            ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item" },
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item" },
            FieldNamesInThisFile: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FieldsByVariable: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase));

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().HaveCount(2);
    }

    [Fact]
    public void Symbol_name_does_NOT_resolve_without_trailing_paren()
    {
        // Naked `Post` (no `(`) could be a variable name, a property key,
        // a namespace segment, etc. Only treat it as a call site when the
        // parens make the intent unambiguous.
        var source = "    var Post: Text; Caption = 'Post';\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void Symbol_name_resolves_with_whitespace_before_paren()
    {
        var source = "    Post (Header);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Using_lines_are_skipped_entirely()
    {
        // `using Microsoft.Sales.Setup;` shouldn't underline `Sales` even
        // if some procedure happens to be called Sales — namespace imports
        // aren't symbol references.
        var source = "using Microsoft.Sales.Setup;\nSales(rec);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Sales"));

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(2, "only the call site on line 2 should match");
    }

    [Fact]
    public void Namespace_lines_are_skipped_entirely()
    {
        var source = "namespace Microsoft.Sales;\n    Sales(rec);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Sales"));

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(2);
    }

    [Theory]
    // Record / table instance methods.
    [InlineData("Get")]
    [InlineData("Find")]
    [InlineData("FindFirst")]
    [InlineData("FindLast")]
    [InlineData("FindSet")]
    [InlineData("Next")]
    [InlineData("SetRange")]
    [InlineData("SetFilter")]
    [InlineData("GetRangeMin")]
    [InlineData("GetRangeMax")]
    [InlineData("Insert")]
    [InlineData("Modify")]
    [InlineData("ModifyAll")]
    [InlineData("Delete")]
    [InlineData("DeleteAll")]
    [InlineData("LockTable")]
    [InlineData("CalcFields")]
    [InlineData("CalcSums")]
    [InlineData("TestField")]
    [InlineData("Reset")]
    [InlineData("Init")]
    [InlineData("Validate")]
    // Dialog / interaction.
    [InlineData("Message")]
    [InlineData("Error")]
    [InlineData("Confirm")]
    [InlineData("StrMenu")]
    // Global system functions.
    [InlineData("Format")]
    [InlineData("Evaluate")]
    [InlineData("Sleep")]
    [InlineData("CalcDate")]
    [InlineData("CreateGuid")]
    [InlineData("Today")]
    [InlineData("CurrentDateTime")]
    [InlineData("StrSubstNo")]
    public void AL_system_functions_are_never_underlined(string name)
    {
        // Even when the user-defined symbol index contains a procedure with
        // the same name (some app wrote a `Format` codeunit), call sites of
        // the built-in dominate; underlining sets the wrong expectation.
        var source = $"    {name}(arg);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols(name));

        ranges.Should().BeEmpty($"AL built-in '{name}' should never be underlined");
    }

    [Fact]
    public void Skips_tokens_inside_line_comments()
    {
        var source = "    // Post is not a call here\n    Post(Header);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(2);
    }

    [Fact]
    public void Skips_tokens_inside_block_comments_spanning_lines()
    {
        var source = "/* ignore\n   Post\n   block */\nPost();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(4);
    }

    [Fact]
    public void Skips_tokens_inside_single_quoted_string_literals()
    {
        var source = "    Lbl := 'Post goes here'; Post();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
        ranges[0].ColumnStart.Should().Be(source.IndexOf("Post(", StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void Ignores_tokens_not_in_either_bucket()
    {
        var source = "Unknown.Method(Otherwise);\n";

        AlResolvableTokenScanner.Scan(source, Symbols("Known")).Should().BeEmpty();
    }

    [Fact]
    public void Matches_are_case_insensitive()
    {
        var source = "    POST(X);\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("Post"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Returns_empty_when_all_buckets_are_empty()
    {
        AlResolvableTokenScanner.Scan("Post();\n", ResolvableVocabulary.Empty)
            .Should().BeEmpty();
    }

    [Fact]
    public void Emits_ranges_in_document_order()
    {
        var source = "B(); A();\nB();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Symbols("A", "B"));

        ranges.Should().HaveCount(3);
        ranges[0].ColumnStart.Should().Be(1);  // B
        ranges[1].ColumnStart.Should().Be(6);  // A
        ranges[2].Line.Should().Be(2);
        ranges[2].ColumnStart.Should().Be(1);  // B
    }

    [Fact]
    public void Object_name_resolves_on_declaration_line_with_numeric_id()
    {
        // `table 36 "Sales Header"` — the numeric ID between the keyword
        // and the name used to break the keyword-context check.
        var source = "table 36 \"Sales Header\"\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Sales Header"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Object_name_resolves_after_tabledata_keyword()
    {
        // `Permissions = tabledata "Assembly Header" = m;` — the `tabledata`
        // property keyword introduces a table-name reference.
        var source = "    Permissions = tabledata \"Assembly Header\" = m;\n";

        var ranges = AlResolvableTokenScanner.Scan(source, Objects("Assembly Header"));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Quoted_field_name_in_table_file_resolves_as_intra_table_reference()
    {
        // Inside a table file, a quoted identifier matching one of this
        // file's fields resolves — covers `"No." := ''`, `Validate("No.")`,
        // `DataCaptionFields = "No.", "Name";`, and other intra-table uses.
        var source = """
                if "No." = '' then
                    Validate("No.");
            """;

        var ranges = AlResolvableTokenScanner.Scan(source, FieldsInFile("No."));

        ranges.Should().HaveCount(2);
    }

    [Fact]
    public void DataCaptionFields_property_resolves_each_quoted_field()
    {
        var source = "    DataCaptionFields = \"No.\", \"Sell-to Customer Name\";\n";

        var ranges = AlResolvableTokenScanner.Scan(
            source, FieldsInFile("No.", "Sell-to Customer Name"));

        ranges.Should().HaveCount(2);
    }

    [Fact]
    public void Bare_field_name_in_table_file_does_NOT_resolve()
    {
        // Quoted form only — bare matches drag in too many false positives
        // (variable names sharing a field's name) without enough payoff.
        var source = "    No := 0;\n";

        var ranges = AlResolvableTokenScanner.Scan(source, FieldsInFile("No"));

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void Quoted_field_inside_string_literal_does_NOT_resolve()
    {
        // The scanner strips string content before tokenising, so `"No."`
        // inside a `Message('No.')` text isn't seen.
        var source = "    Message('No. is required');\n";

        var ranges = AlResolvableTokenScanner.Scan(source, FieldsInFile("No."));

        ranges.Should().BeEmpty();
    }

    [Fact]
    public void Dot_qualified_field_access_resolves_via_FieldsByVariable()
    {
        // `SalesHdr."No."` — variable `SalesHdr` is typed as a Record
        // holding `No.`, so the quoted identifier underlines.
        var source = "    SalesHdr.\"No.\" := '';\n";

        var ranges = AlResolvableTokenScanner.Scan(
            source, FieldsByVar("SalesHdr", "No."));

        ranges.Should().ContainSingle();
    }

    [Fact]
    public void Dot_qualified_access_via_Rec_pseudo_variable_resolves()
    {
        // Inside a table, `Rec."No."` and `xRec."No."` access the current
        // table's own fields. The pseudo-variables aren't in the
        // FieldsByVariable map; they fall back to FieldNamesInThisFile.
        var source = """
                Rec."No." := xRec."No.";
            """;

        var ranges = AlResolvableTokenScanner.Scan(source, FieldsInFile("No."));

        ranges.Should().HaveCount(2);
    }
}
