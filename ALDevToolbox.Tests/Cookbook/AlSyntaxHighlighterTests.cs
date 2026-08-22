using ALDevToolbox.Services.Cookbook;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Covers the recipe-view AL tokenizer (<see cref="AlSyntaxHighlighter"/>). It's
/// a deliberately small classifier, but the recipe detail page renders one span
/// per token, so the class assignment is the contract that drives highlighting.
/// No DB — pure string work.
///
/// <para>Since #587 the classes are the design system's static-code vocabulary
/// — <c>k</c> keyword, <c>t</c> type, <c>s</c> string, <c>o</c> object name,
/// <c>n</c> number, <c>c</c> comment — rather than a private <c>tok-*</c>
/// family, and punctuation and plain words carry no class at all. The pairing
/// of each class to its rule is <c>ComposedClassNameTests</c>'s job; this file
/// covers which class a given piece of AL gets.</para>
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
    public void Keywords_are_classified_k(string word) => One(word).Cls.Should().Be("k");

    [Fact]
    public void Types_are_case_sensitive()
    {
        One("Record").Cls.Should().Be("t");
        // lower-case "record" is not a known type (nor keyword) → plain text
        One("record").Cls.Should().BeEmpty();
    }

    [Fact]
    public void Numbers_strings_identifiers_and_comments_are_classified()
    {
        One("42").Cls.Should().Be("n");
        One("'hello'").Should().Be(("s", "'hello'"));
        One("\"Sales Header\"").Should().Be(("o", "\"Sales Header\""));
        One("// a comment").Should().Be(("c", "// a comment"));
    }

    /// <summary>
    /// The half of the contract that is about NOT painting: punctuation, and a
    /// word that is neither keyword nor type, are emitted with no class so they
    /// inherit the block's own colour. A regression here would be silent — every
    /// run would still render, just tinted by whatever rule the name collided
    /// with — so it is pinned rather than left to the renderer.
    /// </summary>
    [Fact]
    public void Punctuation_and_plain_words_carry_no_class()
    {
        One(" := ").Cls.Should().BeEmpty();
        One("MyLocalVariable").Cls.Should().BeEmpty();
    }

    [Fact]
    public void A_comment_consumes_the_rest_of_the_line()
    {
        var tokens = AlSyntaxHighlighter.TokenizeLine("x := 1; // done");
        tokens.Should().ContainSingle(t => t.Cls == "c" && t.Text == "// done");
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
