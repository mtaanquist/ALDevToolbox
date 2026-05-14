using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Unit tests for <see cref="AlResolvableTokenScanner"/>. Verify that the
/// scanner emits ranges for bare and quoted identifiers, skips comments,
/// strings, and tokens outside the vocabulary, and produces 1-based,
/// end-exclusive columns.
/// </summary>
public sealed class AlResolvableTokenScannerTests
{
    [Fact]
    public void Emits_range_for_bare_identifier_in_vocabulary()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Customer" };
        var source = "    Customer.SetRange(\"No.\", 'X');\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(1);
        ranges[0].ColumnStart.Should().Be(5);
        ranges[0].ColumnEnd.Should().Be(5 + "Customer".Length);
    }

    [Fact]
    public void Emits_range_for_quoted_identifier_with_spaces()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sales Header" };
        var source = "    SalesHeader: Record \"Sales Header\";\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(1);
        ranges[0].ColumnStart.Should().Be(source.IndexOf('"') + 1);
        ranges[0].ColumnEnd.Should().Be(source.LastIndexOf('"') + 2);
    }

    [Fact]
    public void Skips_tokens_inside_line_comments()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Customer" };
        var source = "    // Customer is not a real call here\n    Customer.Foo();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(2);
    }

    [Fact]
    public void Skips_tokens_inside_block_comments_spanning_lines()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Customer" };
        var source = "/* ignore\n   Customer\n   block */\nCustomer.Real();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(4);
    }

    [Fact]
    public void Skips_tokens_inside_single_quoted_string_literals()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Customer" };
        var source = "    Lbl := 'Customer goes here'; Customer.Bar();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        // Only the bare identifier after the string survives.
        ranges.Should().ContainSingle();
        ranges[0].Line.Should().Be(1);
        ranges[0].ColumnStart.Should().Be(source.IndexOf("Customer.", StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void Ignores_tokens_not_in_vocabulary()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Known" };
        var source = "Unknown.Method(Otherwise);\n";

        AlResolvableTokenScanner.Scan(source, vocab).Should().BeEmpty();
    }

    [Fact]
    public void Matches_are_case_insensitive_when_vocabulary_uses_ordinal_ignore_case()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Customer" };
        var source = "    CUSTOMER.Init();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().ContainSingle();
        ranges[0].ColumnStart.Should().Be(5);
    }

    [Fact]
    public void Returns_empty_when_vocabulary_is_empty()
    {
        var vocab = new HashSet<string>();
        AlResolvableTokenScanner.Scan("Customer.Foo();\n", vocab).Should().BeEmpty();
    }

    [Fact]
    public void Emits_ranges_in_document_order()
    {
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var source = "B(); A();\nB();\n";

        var ranges = AlResolvableTokenScanner.Scan(source, vocab);

        ranges.Should().HaveCount(3);
        ranges[0].Line.Should().Be(1);
        ranges[0].ColumnStart.Should().Be(1);  // B
        ranges[1].Line.Should().Be(1);
        ranges[1].ColumnStart.Should().Be(6);  // A
        ranges[2].Line.Should().Be(2);
        ranges[2].ColumnStart.Should().Be(1);  // B
    }
}
