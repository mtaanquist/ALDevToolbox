using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Piper;

/// <summary>
/// Covers <see cref="PiperTransform.Run"/>: the pure transform that backs the
/// <c>/piper</c> page. Each test pins one rule so a regression in delimiter
/// detection, format output, sort behaviour, or escape handling shows up as
/// a focused failure rather than a hard-to-read end-to-end diff.
/// </summary>
public sealed class PiperTransformTests
{
    [Fact]
    public void Empty_input_returns_empty_output_and_awaiting_meta()
    {
        var result = PiperTransform.Run("", new PiperOptions());
        result.Output.Should().BeEmpty();
        result.ItemCount.Should().Be(0);
        result.DelimiterDescription.Should().Be("Awaiting input…");
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    // ---------- Format presets ----------

    [Fact]
    public void BcOr_default_format_joins_with_pipe()
    {
        var result = PiperTransform.Run("a,b,c", new PiperOptions());
        result.Output.Should().Be("a|b|c");
        result.ItemCount.Should().Be(3);
    }

    [Fact]
    public void BcAnd_prefixes_each_item_with_lt_gt_and_joins_with_ampersand()
    {
        var result = PiperTransform.Run("1001,1002,1003",
            new PiperOptions { Format = PiperOutputFormat.BcAnd });
        result.Output.Should().Be("<>1001&<>1002&<>1003");
    }

    [Fact]
    public void Sql_format_wraps_each_item_in_single_quotes_joined_with_comma()
    {
        var result = PiperTransform.Run("a,b,c",
            new PiperOptions { Format = PiperOutputFormat.Sql });
        result.Output.Should().Be("'a','b','c'");
    }

    [Fact]
    public void Sql_format_doubles_embedded_apostrophes()
    {
        // The original piper_app.js used backslash-escape, which produces
        // invalid SQL. The C# port must double the apostrophe instead.
        var result = PiperTransform.Run("O'Brien,Smith",
            new PiperOptions { Format = PiperOutputFormat.Sql });
        result.Output.Should().Be("'O''Brien','Smith'");
    }

    [Fact]
    public void Custom_format_uses_user_supplied_prefix_suffix_join()
    {
        var result = PiperTransform.Run("a,b,c", new PiperOptions
        {
            Format = PiperOutputFormat.Custom,
            CustomPrefix = "[",
            CustomSuffix = "]",
            CustomJoin = " → ",
        });
        result.Output.Should().Be("[a] → [b] → [c]");
    }

    [Fact]
    public void Result_prefix_and_suffix_wrap_the_whole_output_after_format()
    {
        // SQL list inside an IN-clause envelope — the single biggest reason
        // ResultPrefix / ResultSuffix exist.
        var result = PiperTransform.Run("1001,1002,1003", new PiperOptions
        {
            Format = PiperOutputFormat.Sql,
            ResultPrefix = "IN (",
            ResultSuffix = ")",
        });
        result.Output.Should().Be("IN ('1001','1002','1003')");
    }

    // ---------- Auto-detect ----------

    [Theory]
    [InlineData("a,b,c", ",")]
    [InlineData("a;b;c", ";")]
    [InlineData("a\tb\tc", "\t")]
    [InlineData("a|b|c", "|")]
    [InlineData("a\nb\nc", "\n")]
    public void Autodetect_picks_each_candidate_when_it_is_the_only_one_present(string input, string expectedDelim)
    {
        var result = PiperTransform.Run(input, new PiperOptions());
        result.ItemCount.Should().Be(3);
        result.DelimiterDescription.Should().Contain(expectedDelim == "\t" ? "\\t"
            : expectedDelim == "\n" ? "\\n"
            : expectedDelim);
    }

    [Fact]
    public void Autodetect_picks_the_most_prolific_when_multiple_candidates_are_present()
    {
        // Three commas, two semicolons — comma wins.
        var result = PiperTransform.Run("a,b,c,d;e;f", new PiperOptions());
        result.ItemCount.Should().Be(4);
        // Splits on "," → ["a","b","c","d;e;f"], so the semicolons stay inside
        // the last item rather than splitting again.
        result.Output.Should().EndWith("|d;e;f");
    }

    [Fact]
    public void Autodetect_treats_crlf_as_one_separator_not_two_empty_halves()
    {
        // If \n were checked before \r\n, "a\r\nb" would split into
        // ["a\r", "", "b"] and produce a spurious empty item.
        var result = PiperTransform.Run("a\r\nb\r\nc", new PiperOptions());
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void Pipe_is_in_the_autodetect_candidate_set_so_BcOr_output_roundtrips()
    {
        // The "Use result as input" button puts the BC OR output back into
        // the input box. The transform must split on "|" without the user
        // having to type it in the separator override field.
        var result = PiperTransform.Run("a|b|c", new PiperOptions());
        result.Output.Should().Be("a|b|c");
        result.ItemCount.Should().Be(3);
    }

    [Fact]
    public void SplitOnNewlinesOnly_restricts_candidates_so_inline_commas_are_kept()
    {
        var result = PiperTransform.Run("a, b\nc, d", new PiperOptions
        {
            SplitOnNewlinesOnly = true,
        });
        result.ItemCount.Should().Be(2);
        result.Output.Should().Be("a, b|c, d");
    }

    [Fact]
    public void No_delimiter_found_yields_a_single_item_and_clears_detected_separator()
    {
        var result = PiperTransform.Run("loneword", new PiperOptions());
        result.ItemCount.Should().Be(1);
        result.Output.Should().Be("loneword");
        result.DelimiterDescription.Should().Be("no delimiter found");
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Fact]
    public void Detected_separator_display_is_the_escape_form_for_whitespace()
    {
        var result = PiperTransform.Run("a\tb\tc", new PiperOptions());
        result.DetectedSeparatorDisplay.Should().Be("\\t");
    }

    // ---------- Separator override ----------

    [Fact]
    public void Override_preempts_autodetect_and_is_used_literally()
    {
        // Input would auto-detect to "," (most prolific), but the override
        // forces "::" instead.
        var result = PiperTransform.Run("a::b::c,d",
            new PiperOptions { SeparatorOverride = "::" });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c,d");
        result.DelimiterDescription.Should().Be("custom: \"::\"");
        // Override path produces no auto-detect hint.
        result.DetectedSeparatorDisplay.Should().BeNull();
    }

    [Theory]
    [InlineData("\\t", "\t")]
    [InlineData("\\n", "\n")]
    [InlineData("\\r", "\r")]
    [InlineData("\\\\", "\\")]
    public void Override_unescapes_backslash_t_n_r_and_double_backslash(string typed, string actual)
    {
        // The override input is a plain <input type=text>, so the user can't
        // paste raw whitespace conveniently. The escapes let them type "\t".
        var input = $"a{actual}b{actual}c";
        var result = PiperTransform.Run(input,
            new PiperOptions { SeparatorOverride = typed });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void Override_leaves_unknown_backslash_sequences_alone()
    {
        // "\z" isn't a recognised escape — pass it through as the literal
        // two characters so the user isn't silently surprised.
        var result = PiperTransform.Run("a\\zb\\zc",
            new PiperOptions { SeparatorOverride = "\\z" });
        result.ItemCount.Should().Be(3);
        result.Output.Should().Be("a|b|c");
    }

    // ---------- Modifiers ----------

    [Fact]
    public void TrimItems_strips_surrounding_whitespace_per_item()
    {
        var result = PiperTransform.Run("  a , b ,  c  ",
            new PiperOptions { TrimItems = true });
        result.Output.Should().Be("a|b|c");
    }

    [Fact]
    public void SkipEmpty_removes_empties_that_arise_from_adjacent_delimiters()
    {
        // Default keeps empties (matches original Piper behaviour).
        var defaults = PiperTransform.Run("a,,b", new PiperOptions());
        defaults.ItemCount.Should().Be(3);
        defaults.Output.Should().Be("a||b");

        // Opt-in collapses them.
        var skipped = PiperTransform.Run("a,,b",
            new PiperOptions { SkipEmpty = true });
        skipped.ItemCount.Should().Be(2);
        skipped.Output.Should().Be("a|b");
    }

    [Fact]
    public void RemoveDuplicates_preserves_first_occurrence_order()
    {
        var result = PiperTransform.Run("c,a,b,a,c,d",
            new PiperOptions { RemoveDuplicates = true });
        result.Output.Should().Be("c|a|b|d");
    }

    // ---------- Sort ----------

    [Fact]
    public void Sort_ascending_uses_numeric_order_when_all_items_parse_as_decimal()
    {
        // Lexical sort would put "999" after "1001" — wrong for AL ID lists.
        // The transform must detect the all-numeric case and sort numerically.
        var result = PiperTransform.Run("1001,999,1002,50",
            new PiperOptions { Sort = PiperSortOrder.Ascending });
        result.Output.Should().Be("50|999|1001|1002");
    }

    [Fact]
    public void Sort_descending_uses_numeric_order_when_all_items_parse_as_decimal()
    {
        var result = PiperTransform.Run("1001,999,1002,50",
            new PiperOptions { Sort = PiperSortOrder.Descending });
        result.Output.Should().Be("1002|1001|999|50");
    }

    [Fact]
    public void Sort_falls_back_to_lexical_when_any_item_is_not_numeric()
    {
        // One non-numeric item — every item gets compared lexically.
        var result = PiperTransform.Run("banana,apple,Cherry",
            new PiperOptions { Sort = PiperSortOrder.Ascending });
        result.Output.Should().Be("apple|banana|Cherry");
    }

    [Fact]
    public void Sort_none_keeps_input_order()
    {
        var result = PiperTransform.Run("c,a,b",
            new PiperOptions { Sort = PiperSortOrder.None });
        result.Output.Should().Be("c|a|b");
    }

    // ---------- Pipeline ordering ----------

    [Fact]
    public void Modifiers_apply_in_order_trim_then_skip_empty_then_dedup_then_sort()
    {
        // " 2 ,, 1, 2 , 3 " trimmed → ["2","","1","2","3"]
        //                  skip empty → ["2","1","2","3"]
        //                       dedup → ["2","1","3"]
        //                  sort asc → ["1","2","3"]
        // BC OR format → "1|2|3"
        var result = PiperTransform.Run(" 2 ,, 1, 2 , 3 ", new PiperOptions
        {
            TrimItems = true,
            SkipEmpty = true,
            RemoveDuplicates = true,
            Sort = PiperSortOrder.Ascending,
        });
        result.Output.Should().Be("1|2|3");
    }
}
