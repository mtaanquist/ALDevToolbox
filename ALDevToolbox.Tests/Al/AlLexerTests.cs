using ALDevToolbox.Services.Al;
using FluentAssertions;

namespace ALDevToolbox.Tests.Al;

public sealed class AlLexerTests
{
    [Fact]
    public void Tokenizes_a_simple_member_access()
    {
        var tokens = AlLexer.Tokenize("SalesLine.InitRecord();");

        tokens.Should().HaveCount(6);
        tokens.Select(t => (t.Kind, t.Value)).Should().ContainInOrder(
            (AlTokenKind.Identifier, "SalesLine"),
            (AlTokenKind.Punct, "."),
            (AlTokenKind.Identifier, "InitRecord"),
            (AlTokenKind.Punct, "("),
            (AlTokenKind.Punct, ")"),
            (AlTokenKind.Punct, ";"));
        tokens[0].Line.Should().Be(1);
        tokens[0].Column.Should().Be(1);
    }

    [Fact]
    public void Strips_quotes_from_quoted_identifiers()
    {
        var tokens = AlLexer.Tokenize("\"Sales Line\".\"No.\"");

        tokens[0].Kind.Should().Be(AlTokenKind.QuotedIdentifier);
        tokens[0].Value.Should().Be("Sales Line");
        tokens[1].Value.Should().Be(".");
        tokens[2].Kind.Should().Be(AlTokenKind.QuotedIdentifier);
        tokens[2].Value.Should().Be("No.");
    }

    [Fact]
    public void Recognises_multi_char_operators()
    {
        var tokens = AlLexer.Tokenize("a := b :: c <> d <= e >= f .. g");

        var kinds = tokens.Select(t => t.Kind).ToArray();
        kinds.Should().ContainInOrder(
            AlTokenKind.Identifier, AlTokenKind.Assign,
            AlTokenKind.Identifier, AlTokenKind.DoubleColon,
            AlTokenKind.Identifier, AlTokenKind.Compare,
            AlTokenKind.Identifier, AlTokenKind.Compare,
            AlTokenKind.Identifier, AlTokenKind.Compare,
            AlTokenKind.Identifier, AlTokenKind.Range,
            AlTokenKind.Identifier);
    }

    [Fact]
    public void Consumes_line_comments_without_emitting_tokens()
    {
        var tokens = AlLexer.Tokenize("a // a comment\nb");
        tokens.Should().HaveCount(2);
        tokens[0].Value.Should().Be("a");
        tokens[1].Value.Should().Be("b");
        tokens[1].Line.Should().Be(2);
    }

    [Fact]
    public void Consumes_block_comments_and_keeps_line_count()
    {
        var tokens = AlLexer.Tokenize("a /* skip\nthis\nblock */ b");

        tokens.Should().HaveCount(2);
        tokens[0].Value.Should().Be("a");
        tokens[1].Value.Should().Be("b");
        tokens[1].Line.Should().Be(3,
            because: "the newlines inside the block comment still advance the line counter");
    }

    [Fact]
    public void Single_quoted_strings_swallow_inner_doubled_quotes()
    {
        var tokens = AlLexer.Tokenize("'it''s a test'");

        tokens.Should().ContainSingle();
        tokens[0].Kind.Should().Be(AlTokenKind.String);
        tokens[0].Value.Should().Be("'it''s a test'");
    }

    [Fact]
    public void Tracks_line_and_column_across_newlines()
    {
        var tokens = AlLexer.Tokenize("foo\nbar\n  baz");
        tokens.Select(t => (t.Value, t.Line, t.Column))
            .Should().ContainInOrder(
                ("foo", 1, 1),
                ("bar", 2, 1),
                ("baz", 3, 3));
    }

    [Fact]
    public void Pragma_directive_is_one_token_to_end_of_line()
    {
        var tokens = AlLexer.Tokenize("#pragma warning disable AA0074\nfoo");
        tokens.Should().HaveCount(2);
        tokens[0].Kind.Should().Be(AlTokenKind.Directive);
        tokens[0].Value.Should().Be("#pragma warning disable AA0074");
        tokens[1].Value.Should().Be("foo");
        tokens[1].Line.Should().Be(2);
    }

    [Fact]
    public void Tokenizes_a_realistic_procedure_call_with_chained_field_access()
    {
        // The kind of statement the reference extractor will care about:
        // procedure call on a record variable that's a field on another
        // record. Two member accesses chained — the lexer just emits
        // tokens, the consumer is the one that resolves types.
        var tokens = AlLexer.Tokenize("SalesHeader.\"Sell-to Customer No.\" := Cust.\"No.\";");

        tokens.Select(t => (t.Kind, t.Value))
            .Should().ContainInOrder(
                (AlTokenKind.Identifier, "SalesHeader"),
                (AlTokenKind.Punct, "."),
                (AlTokenKind.QuotedIdentifier, "Sell-to Customer No."),
                (AlTokenKind.Assign, ":="),
                (AlTokenKind.Identifier, "Cust"),
                (AlTokenKind.Punct, "."),
                (AlTokenKind.QuotedIdentifier, "No."));
    }

    [Fact]
    public void Number_literal_with_decimal_point_is_a_single_token()
    {
        var tokens = AlLexer.Tokenize("a = 3.14");
        tokens.Last().Kind.Should().Be(AlTokenKind.Number);
        tokens.Last().Value.Should().Be("3.14");
    }

    [Fact]
    public void Hex_literal_is_a_single_number_token()
    {
        var tokens = AlLexer.Tokenize("v := 0xDEADBEEF;");
        tokens.Should().Contain(t => t.Kind == AlTokenKind.Number && t.Value == "0xDEADBEEF");
    }

    [Fact]
    public void Hex_prefix_without_digits_treats_zero_as_number_and_x_as_identifier()
    {
        // Defensive: `0xy` isn't valid AL hex (no digits after the
        // prefix). The lexer falls back to `0` + `xy` rather than
        // mis-claim a malformed hex token.
        var tokens = AlLexer.Tokenize("0xy");
        tokens.Should().HaveCount(2);
        tokens[0].Kind.Should().Be(AlTokenKind.Number);
        tokens[0].Value.Should().Be("0");
        tokens[1].Kind.Should().Be(AlTokenKind.Identifier);
        tokens[1].Value.Should().Be("xy");
    }

    [Fact]
    public void Scientific_notation_is_a_single_number_token()
    {
        var cases = new[]
        {
            ("1e6", "1e6"),
            ("2.5e-3", "2.5e-3"),
            ("1.0E+10", "1.0E+10"),
        };
        foreach (var (src, expected) in cases)
        {
            var tokens = AlLexer.Tokenize(src);
            tokens.Should().ContainSingle(because: $"`{src}` is one number token");
            tokens[0].Kind.Should().Be(AlTokenKind.Number);
            tokens[0].Value.Should().Be(expected);
        }
    }

    [Fact]
    public void Numeric_suffix_is_part_of_the_number_token()
    {
        var cases = new[] { "42L", "100U", "3.14F", "100ll", "1ULL" };
        foreach (var src in cases)
        {
            var tokens = AlLexer.Tokenize(src);
            tokens.Should().ContainSingle(because: $"`{src}` is one number token with suffix");
            tokens[0].Kind.Should().Be(AlTokenKind.Number);
            tokens[0].Value.Should().Be(src);
        }
    }

    [Fact]
    public void Decimal_point_member_access_is_not_a_decimal_literal()
    {
        // `42.ToString` would historically have been ambiguous — the
        // lexer must NOT consume the dot as part of the number, or
        // the chained member access would lose its operator.
        var tokens = AlLexer.Tokenize("42.ToString");
        tokens.Should().HaveCount(3);
        tokens[0].Kind.Should().Be(AlTokenKind.Number);
        tokens[0].Value.Should().Be("42");
        tokens[1].Kind.Should().Be(AlTokenKind.Punct);
        tokens[1].Value.Should().Be(".");
        tokens[2].Kind.Should().Be(AlTokenKind.Identifier);
        tokens[2].Value.Should().Be("ToString");
    }

    [Fact]
    public void Compound_assignment_operators_are_one_token_each()
    {
        var tokens = AlLexer.Tokenize("a += 1; b -= 2; c *= 3; d /= 4;");
        var ops = tokens.Where(t => t.Kind == AlTokenKind.CompoundAssign)
            .Select(t => t.Value)
            .ToArray();
        ops.Should().Equal(new[] { "+=", "-=", "*=", "/=" });
    }

    [Fact]
    public void Compound_assign_slash_equals_does_not_collide_with_comment_starts()
    {
        // `/=` must lex as CompoundAssign, never as the start of a `//`
        // line comment or `/*` block comment.
        var tokens = AlLexer.Tokenize("a /= 2;");
        tokens.Should().Contain(t => t.Kind == AlTokenKind.CompoundAssign && t.Value == "/=");
        tokens.Should().NotContain(t => t.Kind == AlTokenKind.Punct && t.Value == "/");
    }

    [Fact]
    public void Empty_or_whitespace_only_input_yields_no_tokens()
    {
        AlLexer.Tokenize("").Should().BeEmpty();
        AlLexer.Tokenize("   \n  \t  \n").Should().BeEmpty();
    }

    [Fact]
    public void Procedure_declaration_with_parameters_and_local_var_block()
    {
        var src = """
            procedure Foo(SalesLine: Record "Sales Line"; var Result: Boolean)
            var
                Cust: Record Customer;
            begin
                Cust.Get(SalesLine."Sell-to Customer No.");
            end;
            """;
        var tokens = AlLexer.Tokenize(src);

        // Sanity: the procedure / var / begin / end keywords come through
        // as identifiers (the lexer doesn't classify keywords; the
        // consumer matches on Value).
        tokens.Should().Contain(t => t.Value == "procedure" && t.Kind == AlTokenKind.Identifier);
        tokens.Should().Contain(t => t.Value == "var" && t.Kind == AlTokenKind.Identifier);
        tokens.Should().Contain(t => t.Value == "begin" && t.Kind == AlTokenKind.Identifier);
        tokens.Should().Contain(t => t.Value == "end" && t.Kind == AlTokenKind.Identifier);
        // The Customer record .Get call comes through as a separable chain.
        tokens.Should().Contain(t => t.Value == "Get" && t.Kind == AlTokenKind.Identifier);
        tokens.Should().Contain(t => t.Value == "Sell-to Customer No."
            && t.Kind == AlTokenKind.QuotedIdentifier);
    }
}
