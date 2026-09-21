using ALDevToolbox.Services.Palette;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The parsing and folding half of <c>.design/command-palette.md</c>'s
/// "Matching and ranking": what counts as a searchable query, how it is split
/// into terms, and the accent folding that makes <c>moller</c> find "Møller".
/// </summary>
public sealed class PaletteQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("c")]
    [InlineData("  c  ")]
    public void A_query_under_two_characters_is_not_searched(string? raw)
    {
        PaletteQuery.Parse(raw).IsUsable.Should().BeFalse(
            "one character matches most of an organisation and would cost a round trip per keystroke");
    }

    [Fact]
    public void A_query_over_a_hundred_characters_is_not_searched()
    {
        PaletteQuery.Parse(new string('x', PaletteQuery.MaxLength)).IsUsable.Should().BeTrue();
        PaletteQuery.Parse(new string('x', PaletteQuery.MaxLength + 1)).IsUsable.Should().BeFalse(
            "past a hundred characters it is a paste, not a search");
    }

    [Fact]
    public void The_length_is_measured_after_trimming()
    {
        PaletteQuery.Parse("  " + new string('x', PaletteQuery.MaxLength) + "  ").IsUsable
            .Should().BeTrue("surrounding whitespace is not part of what the user typed");
    }

    [Fact]
    public void Whitespace_splits_the_query_into_terms()
    {
        var query = PaletteQuery.Parse("  Contoso   Coffee \t Production ");

        query.Terms.Should().Equal("contoso", "coffee", "production");
        query.Raw.Should().Be("Contoso   Coffee \t Production", "the raw text is only trimmed");
        query.Folded.Should().Be("contoso coffee production",
            "the whole-query form collapses whitespace so the prefix tier can compare it to a title");
    }

    [Fact]
    public void A_repeated_term_is_only_carried_once()
    {
        PaletteQuery.Parse("con CON con").Terms.Should().Equal(
            new[] { "con" }, "matching the same term three times costs three passes and proves nothing");
    }

    [Theory]
    [InlineData("Møller", "moller")]
    [InlineData("Jørgensen Møbler", "jorgensen mobler")]
    [InlineData("Café", "cafe")]
    [InlineData("Ångström", "angstrom")]
    [InlineData("Ñuñoa", "nunoa")]
    [InlineData("Æblegård", "aeblegard")]
    [InlineData("Straße", "strasse")]
    [InlineData("Þór", "thor")]
    [InlineData("Łódź", "lodz")]
    public void Folding_removes_case_and_accents(string value, string expected)
    {
        PaletteQuery.Fold(value).Should().Be(expected);
    }

    [Fact]
    public void A_folded_query_finds_an_accented_name_and_the_other_way_round()
    {
        // Both directions matter: the customer is spelled with the accent in the
        // database, and the consultant types whichever is quicker.
        PaletteQuery.Parse("moller").Terms.Should().Equal(PaletteQuery.Fold("Møller"));
        PaletteQuery.Parse("Møller").Terms.Should().Equal(PaletteQuery.Fold("moller"));
    }

    [Fact]
    public void Sql_terms_keep_their_accents_because_postgres_cannot_fold_them()
    {
        // unaccent is not installed, so a source pre-filtering with ILike has to
        // compare against what is actually in the column. Documented on
        // PaletteQuery.SqlTerms, and the reason a source over human-typed names
        // should not pre-filter at all.
        var query = PaletteQuery.Parse("Møller Holding");

        query.SqlTerms.Should().Equal("møller", "holding");
        query.Terms.Should().Equal("moller", "holding");
    }

    [Fact]
    public void Folding_survives_an_unpaired_surrogate()
    {
        // Reaches us straight off a query string; nothing to fold, but nothing
        // worth a 500 either.
        var act = () => PaletteQuery.Parse("con \ud800 cof");
        act.Should().NotThrow();
    }
}
