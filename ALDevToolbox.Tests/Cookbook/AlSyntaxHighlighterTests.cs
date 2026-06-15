using ALDevToolbox.Services.Cookbook;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Covers the recipe-view AL tokenizer (<see cref="AlSyntaxHighlighter"/>). It's
/// a deliberately small classifier, but the recipe detail page renders one span
/// per token, so the class assignment (keyword / type / string / identifier /
/// number / comment / punctuation) is the contract that drives highlighting.
/// No DB — pure string work.
/// </summary>
public sealed class AlSyntaxHighlighterTests
{
    private static (string Cls, string Text) One(string line)
    {
        var tokens = AlSyntaxHighlighter.TokenizeLine(line);
        tokens.Should().HaveCount(1);
        return (tokens[0].Cls, tokens[0].Text);
    }

    [Theory]
    [InlineData("procedure")]
    [InlineData("PROCEDURE")] // keywords are case-insensitive
    [InlineData("begin")]
    [InlineData("if")]
    public void Keywords_are_classified_kw(string word) => One(word).Cls.Should().Be("kw");

    [Fact]
    public void Types_are_case_sensitive()
    {
        One("Record").Cls.Should().Be("type");
        // lower-case "record" is not a known type (nor keyword) → plain text
        One("record").Cls.Should().Be("txt");
    }

    [Fact]
    public void Numbers_strings_identifiers_and_comments_are_classified()
    {
        One("42").Cls.Should().Be("num");
        One("'hello'").Should().Be(("str", "'hello'"));
        One("\"Sales Header\"").Should().Be(("id", "\"Sales Header\""));
        One("// a comment").Should().Be(("com", "// a comment"));
    }

    [Fact]
    public void A_comment_consumes_the_rest_of_the_line()
    {
        var tokens = AlSyntaxHighlighter.TokenizeLine("x := 1; // done");
        tokens.Should().ContainSingle(t => t.Cls == "com" && t.Text == "// done");
        tokens[^1].Text.Should().Be("// done");
    }

    [Fact]
    public void Tokens_reassemble_to_the_original_line()
    {
        const string line = "    if Rec.\"No.\" = '' then exit(0); // guard";
        var joined = string.Concat(AlSyntaxHighlighter.TokenizeLine(line).Select(t => t.Text));
        joined.Should().Be(line);
    }

    [Fact]
    public void Empty_line_yields_no_tokens()
    {
        AlSyntaxHighlighter.TokenizeLine(string.Empty).Should().BeEmpty();
    }
}
