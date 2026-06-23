using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Cookbook;

/// <summary>
/// Unit coverage for <see cref="RecipeService.NormaliseKeywords"/>: the
/// quote-aware tokeniser that lets a multi-word tag survive as one. Tags are
/// stored comma-separated; a double-quoted phrase keeps its internal spaces.
/// </summary>
public sealed class RecipeKeywordTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("factbox", "factbox")]
    [InlineData("Factbox Attachment Subscriber", "factbox,attachment,subscriber")]
    [InlineData("posting, validation", "posting,validation")]
    [InlineData("  spaced   out  ", "spaced,out")]
    public void Single_word_tokens_are_lowercased_and_comma_joined(string? input, string expected)
    {
        RecipeService.NormaliseKeywords(input).Should().Be(expected);
    }

    [Fact]
    public void Quoted_phrase_becomes_one_tag()
    {
        RecipeService.NormaliseKeywords("\"document attachments\" factbox subscriber")
            .Should().Be("document attachments,factbox,subscriber");
    }

    [Fact]
    public void Mixed_quoted_and_unquoted_with_commas()
    {
        RecipeService.NormaliseKeywords("posting, \"sales order\", validation")
            .Should().Be("posting,sales order,validation");
    }

    [Fact]
    public void Internal_whitespace_in_a_quoted_phrase_is_collapsed()
    {
        RecipeService.NormaliseKeywords("\"document    attachments\"")
            .Should().Be("document attachments");
    }

    [Fact]
    public void Comma_inside_a_quoted_phrase_is_folded_to_a_space_so_it_cannot_split_the_tag()
    {
        // The comma is the storage delimiter; a stray comma inside a phrase
        // must not survive or it would read back as two tags.
        RecipeService.NormaliseKeywords("\"sales, order\"")
            .Should().Be("sales order");
    }

    [Fact]
    public void Duplicate_tags_are_removed_keeping_first_seen_order()
    {
        RecipeService.NormaliseKeywords("factbox Factbox \"doc attach\" \"doc attach\"")
            .Should().Be("factbox,doc attach");
    }
}
