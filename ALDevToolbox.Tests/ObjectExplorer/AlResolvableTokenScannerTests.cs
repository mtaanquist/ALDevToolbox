using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="AlResolvableTokenScanner"/>. Pins the two
/// resolution rules: <em>symbol names</em> resolve standalone (procedure
/// calls, event handlers), <em>object names</em> only resolve in a
/// keyword-preceded context (<c>Record "Sales Header"</c>,
/// <c>Codeunit::"Sales-Post"</c>) so a stray variable named <c>Item</c>
/// doesn't get a misleading underline.
/// </summary>
public sealed class AlResolvableTokenScannerTests
{
    private static ResolvableVocabulary Symbols(params string[] names) =>
        new(ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));

    private static ResolvableVocabulary Objects(params string[] names) =>
        new(ObjectNames: new HashSet<string>(names, StringComparer.OrdinalIgnoreCase),
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

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
        // `Item` is both an object (table) and a symbol (procedure) — the
        // standalone bucket wins, so any call site resolves regardless of
        // whether a keyword precedes it.
        var source = "    Item.Get(); Cu: Codeunit Item;\n";
        var vocab = new ResolvableVocabulary(
            ObjectNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item" },
            SymbolNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item" });

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().HaveCount(2);
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
    public void Returns_empty_when_both_buckets_are_empty()
    {
        var vocab = new ResolvableVocabulary(
            ObjectNames: new HashSet<string>(),
            SymbolNames: new HashSet<string>());

        AlResolvableTokenScanner.Scan("Post();\n", vocab).Should().BeEmpty();
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
}
