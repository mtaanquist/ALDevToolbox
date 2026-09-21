using ALDevToolbox.Services.Palette;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The ranking contract from <c>.design/command-palette.md</c>: every term must
/// match somewhere in any order, the four tiers in their order, ties broken by
/// name, and anything that does not match dropped.
///
/// <para>Pure and exhaustive on purpose - this is the one function every source
/// funnels through, so a change to it changes every list in the palette at once,
/// and no source test would notice.</para>
/// </summary>
public sealed class PaletteRankingTests
{
    private static PaletteCandidate Solution(string title, string? shortName = null, string? subtitle = null) =>
        new("solution", title, subtitle, "/solutions/1", shortName);

    private static PaletteMatchTier? Match(string query, PaletteCandidate candidate) =>
        PaletteRanking.Match(PaletteQuery.Parse(query), candidate);

    // ── Every term must match, in any order ─────────────────────────────

    [Theory]
    [InlineData("con cof")]
    [InlineData("cof con")]
    [InlineData("contoso coffee")]
    [InlineData("coffee contoso")]
    public void Every_term_matches_in_any_order(string query)
    {
        Match(query, Solution("Contoso Coffee")).Should().NotBeNull();
    }

    [Fact]
    public void A_term_that_matches_nothing_drops_the_whole_candidate()
    {
        Match("con tea", Solution("Contoso Coffee")).Should().BeNull(
            "every term has to match somewhere, or the row is not what was asked for");
    }

    [Fact]
    public void Terms_may_match_across_different_fields()
    {
        // The worked example from the design doc: an environment's subtitle is
        // its Solution, so "con prod" finds Contoso's Production environment.
        var environment = new PaletteCandidate(
            "environment", "Production", "Contoso Coffee", "/solutions/1/environments/2");

        Match("con prod", environment).Should().NotBeNull();
    }

    [Fact]
    public void The_short_name_is_a_searched_field()
    {
        Match("concof", Solution("Contoso Coffee", shortName: "CONCOF")).Should().NotBeNull();
    }

    [Fact]
    public void Matching_ignores_accents_on_both_sides()
    {
        Match("moller", Solution("Jørgensen Møller")).Should().NotBeNull();
        Match("møller", Solution("Jorgensen Moller")).Should().NotBeNull();
    }

    // ── The four tiers ──────────────────────────────────────────────────

    [Fact]
    public void An_exact_short_name_is_the_top_tier()
    {
        Match("concof", Solution("Contoso Coffee", shortName: "CONCOF"))
            .Should().Be(PaletteMatchTier.ExactShortName);
    }

    [Fact]
    public void A_short_name_that_only_starts_the_query_is_not_the_top_tier()
    {
        Match("con", Solution("Contoso Coffee", shortName: "CONCOF"))
            .Should().Be(PaletteMatchTier.QueryPrefix, "'con' is a prefix of CONCOF, not CONCOF itself");
    }

    [Fact]
    public void The_whole_query_being_a_prefix_of_the_title_beats_a_word_start()
    {
        Match("contoso c", Solution("Contoso Coffee")).Should().Be(PaletteMatchTier.QueryPrefix);
        Match("cont cof", Solution("Contoso Coffee")).Should().Be(PaletteMatchTier.WordStart);
    }

    [Fact]
    public void Every_term_at_a_word_start_beats_a_bare_substring()
    {
        Match("con cof", Solution("Contoso Coffee")).Should().Be(PaletteMatchTier.WordStart);
        Match("ontoso offee", Solution("Contoso Coffee")).Should().Be(PaletteMatchTier.Substring);
    }

    [Fact]
    public void One_term_off_a_word_start_drops_the_row_to_the_substring_tier()
    {
        Match("con offee", Solution("Contoso Coffee")).Should().Be(PaletteMatchTier.Substring,
            "the tier is the weakest of the terms, not the strongest");
    }

    [Theory]
    [InlineData("Contoso-Coffee")]
    [InlineData("Contoso (Coffee)")]
    [InlineData("Contoso/Coffee")]
    public void A_word_starts_after_punctuation_too(string title)
    {
        Match("cof", Solution(title)).Should().Be(PaletteMatchTier.WordStart);
    }

    [Fact]
    public void A_word_start_in_the_subtitle_counts()
    {
        var environment = new PaletteCandidate(
            "environment", "Production", "Contoso Coffee", "/solutions/1/environments/2");

        Match("prod con", environment).Should().Be(PaletteMatchTier.WordStart);
    }

    // ── Ordering and dropping ───────────────────────────────────────────

    [Fact]
    public void Rank_orders_by_tier_then_by_title_and_drops_non_matches()
    {
        var query = PaletteQuery.Parse("con");
        var candidates = new[]
        {
            Solution("Zeta Contoso"),              // substring / word start
            Solution("Contoso Zulu"),              // prefix
            Solution("Alpha Consulting"),          // word start
            Solution("Barrel Bacon", shortName: "CON"), // exact short name
            Solution("Nothing Here"),              // dropped
            Solution("Contoso Alpha"),             // prefix, sorts before Contoso Zulu
        };

        var ranked = PaletteRanking.Rank(query, candidates);

        ranked.Select(m => m.Candidate.Title).Should().Equal(
            "Barrel Bacon", "Contoso Alpha", "Contoso Zulu", "Alpha Consulting", "Zeta Contoso");
        ranked[0].Tier.Should().Be(PaletteMatchTier.ExactShortName);
        ranked.Should().NotContain(m => m.Candidate.Title == "Nothing Here");
    }

    [Fact]
    public void Ties_break_by_name_not_by_the_order_the_source_returned_them()
    {
        var query = PaletteQuery.Parse("cron");
        var ranked = PaletteRanking.Rank(query, new[]
        {
            Solution("CRONUS Norway"),
            Solution("CRONUS Denmark"),
            Solution("CRONUS Sweden"),
        });

        ranked.Select(m => m.Candidate.Title).Should().Equal(
            new[] { "CRONUS Denmark", "CRONUS Norway", "CRONUS Sweden" },
            "a list that reorders itself between two identical queries cannot be learned");
    }

    [Fact]
    public void An_unusable_query_matches_nothing()
    {
        PaletteRanking.Match(PaletteQuery.Unusable, Solution("Contoso Coffee")).Should().BeNull();
        PaletteRanking.Rank(PaletteQuery.Unusable, new[] { Solution("Contoso Coffee") }).Should().BeEmpty();
    }

    [Fact]
    public void A_candidate_with_no_short_name_or_subtitle_still_ranks_on_its_title()
    {
        Match("cof", new PaletteCandidate("recipe", "Coffee grinder", null, "/cookbook/4"))
            .Should().Be(PaletteMatchTier.QueryPrefix);
    }
}
