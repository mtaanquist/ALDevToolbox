using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Pins the naming table in <c>.design/customer-naming.md</c>: a customer name
/// is typed once, in whatever script the customer's name is actually written
/// in, and every machine name comes out of it. The Scandinavian letters are the
/// point of the exercise - "Jørgensen Møbler" used to be refused outright - so
/// they are what the table is built around.
/// </summary>
public sealed class CustomerNamingTests
{
    private const string Jorgensen = "Jørgensen Møbler A/S";

    [Theory]
    [InlineData(NamingStyle.PascalCase, "JorgensenMoblerAS")]
    [InlineData(NamingStyle.CamelCase, "jorgensenMoblerAS")]
    [InlineData(NamingStyle.KebabCase, "jorgensen-mobler-a-s")]
    [InlineData(NamingStyle.SnakeCase, "jorgensen_mobler_a_s")]
    [InlineData(NamingStyle.Lowercase, "jorgensenmobleras")]
    [InlineData(NamingStyle.None, "Jorgensen Mobler A S")]
    public void Every_style_derives_the_documented_name(NamingStyle style, string expected)
    {
        CustomerNaming.Apply(Jorgensen, style).Should().Be(expected);
    }

    [Theory]
    // The letters that survive canonical decomposition intact, so the explicit
    // table is the only thing that handles them.
    [InlineData("Åse Møller", "AaseMoller")]
    [InlineData("Æblegård", "Aeblegaard")]
    [InlineData("Straße Handel", "StrasseHandel")]
    [InlineData("Œuvre Þor Łódź Đakovo", "OeuvreThorLodzDakovo")]
    // A letter that expands to two only stays shouted when the word is.
    [InlineData("ÅRHUS ÆRØ", "AARHUSAERO")]
    // Decomposition alone handles the rest.
    [InlineData("Café Ñandú Müller", "CafeNanduMuller")]
    public void Letters_outside_ascii_transliterate(string typed, string expected)
    {
        CustomerNaming.Apply(typed, NamingStyle.PascalCase).Should().Be(expected);
    }

    [Fact]
    public void A_decomposed_letter_transliterates_like_a_composed_one()
    {
        // "Å" typed as A + combining ring above, which some keyboards and some
        // pasted text produce.
        CustomerNaming.Apply("Åse", NamingStyle.PascalCase).Should().Be("Aase");
    }

    [Theory]
    // Word boundaries are separators only, so a run that is already one word
    // keeps the casing it was typed with under PascalCase - and loses it under
    // the lowercase styles.
    [InlineData(NamingStyle.PascalCase, "CRONUSCustomer")]
    [InlineData(NamingStyle.CamelCase, "cRONUSCustomer")]
    [InlineData(NamingStyle.KebabCase, "cronus-customer")]
    [InlineData(NamingStyle.Lowercase, "cronuscustomer")]
    [InlineData(NamingStyle.None, "CRONUS Customer")]
    public void An_existing_run_is_not_re_cased(NamingStyle style, string expected)
    {
        CustomerNaming.Apply("CRONUS Customer", style).Should().Be(expected);
    }

    [Theory]
    [InlineData("cronus customer", "CronusCustomer")]
    [InlineData("  CRONUS   A/S  ", "CRONUSAS")]
    [InlineData("CRONUS 2 Ltd.", "CRONUS2Ltd")]
    public void Separators_are_the_only_word_boundary(string typed, string expected)
    {
        CustomerNaming.Apply(typed, NamingStyle.PascalCase).Should().Be(expected);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("--- / ---")]
    public void A_name_with_nothing_to_name_with_is_refused(string? typed)
    {
        CustomerNaming.HasNameCharacters(typed).Should().BeFalse();
        CustomerNaming.Apply(typed, NamingStyle.PascalCase).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Jørgensen Møbler")]
    [InlineData("!!! 7 !!!")]
    [InlineData("Ø")]
    public void A_name_with_a_letter_or_digit_left_is_accepted(string typed)
    {
        CustomerNaming.HasNameCharacters(typed).Should().BeTrue();
    }

    [Fact]
    public void A_long_name_is_cut_to_a_hundred_characters()
    {
        var typed = string.Join(' ', Enumerable.Repeat("Jorgensen", 20));

        var pascal = CustomerNaming.Apply(typed, NamingStyle.PascalCase);

        pascal.Should().HaveLength(CustomerNaming.MaxLength);
        pascal.Should().StartWith("JorgensenJorgensen");
    }

    [Theory]
    [InlineData(NamingStyle.KebabCase, '-')]
    [InlineData(NamingStyle.SnakeCase, '_')]
    [InlineData(NamingStyle.None, ' ')]
    public void The_cut_never_leaves_a_dangling_separator(NamingStyle style, char separator)
    {
        // 34 three-letter words: the 34th word ends at character 101, so the
        // cut lands inside it and the separator before it would otherwise be
        // the last character kept.
        var typed = string.Join(' ', Enumerable.Repeat("abc", 34));

        var name = CustomerNaming.Apply(typed, style);

        name.Length.Should().BeLessThanOrEqualTo(CustomerNaming.MaxLength);
        name.Should().NotEndWith(separator.ToString());
    }
}
