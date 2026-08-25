using ALDevToolbox.Domain.ValueObjects;
using FluentAssertions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The warning window an admin's notice period depends on. Every boundary here
/// is one an operator would only discover the hard way: a secret that lapses
/// silently takes Microsoft sign-in down with it.
/// </summary>
public sealed class EntraSecretExpiryTests
{
    private static readonly DateOnly Today = new(2026, 8, 25);

    [Fact]
    public void No_recorded_date_is_unknown_and_never_warns()
    {
        var expiry = EntraSecretExpiry.From(null, Today);

        expiry.State.Should().Be(EntraSecretExpiryState.Unknown);
        expiry.NeedsAttention.Should().BeFalse("there is nothing to warn about until someone notes a date");
    }

    [Theory]
    // One day outside the window is the last quiet day.
    [InlineData(EntraSecretExpiry.WarningWindowDays + 1, EntraSecretExpiryState.Ok, false)]
    // The window is inclusive at its far edge - this is the day the warning starts.
    [InlineData(EntraSecretExpiry.WarningWindowDays, EntraSecretExpiryState.Expiring, true)]
    [InlineData(1, EntraSecretExpiryState.Expiring, true)]
    // Expiry day itself still counts as expiring, not expired: the secret works
    // until Entra says otherwise, and "expired today" would be a lie in the morning.
    [InlineData(0, EntraSecretExpiryState.Expiring, true)]
    [InlineData(-1, EntraSecretExpiryState.Expired, true)]
    [InlineData(-90, EntraSecretExpiryState.Expired, true)]
    public void Days_from_today_decide_the_state(int offsetDays, EntraSecretExpiryState expected, bool warns)
    {
        var expiry = EntraSecretExpiry.From(Today.AddDays(offsetDays), Today);

        expiry.State.Should().Be(expected);
        expiry.DaysRemaining.Should().Be(offsetDays);
        expiry.NeedsAttention.Should().Be(warns);
    }

    [Fact]
    public void A_far_off_date_is_healthy()
    {
        EntraSecretExpiry.From(Today.AddYears(2), Today).State
            .Should().Be(EntraSecretExpiryState.Ok);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("2027-01-14", "2027-01-14")]
    // Leading/trailing whitespace survives a copy-paste from the Entra portal.
    [InlineData(" 2027-01-14 ", "2027-01-14")]
    public void Blank_and_iso_dates_parse(string? raw, string? expected)
    {
        EntraSecretExpiry.TryParseInput(raw, out var date).Should().BeTrue();
        date?.ToString("yyyy-MM-dd").Should().Be(expected);
    }

    [Theory]
    [InlineData("14/01/2027")]
    [InlineData("Jan 14 2027")]
    [InlineData("2027-13-01")]
    [InlineData("nonsense")]
    public void Anything_that_is_not_an_iso_date_is_rejected(string raw)
    {
        EntraSecretExpiry.TryParseInput(raw, out var date).Should().BeFalse();
        date.Should().BeNull();
    }
}
