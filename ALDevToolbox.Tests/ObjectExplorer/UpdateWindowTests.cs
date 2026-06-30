using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using FluentAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The pure window math behind delivery scheduling: same-day and midnight-wrapping
/// windows, "no window = any time", and the next-opening prefill, in UTC and in an
/// offset timezone. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class UpdateWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // A fixed +02:00 zone (no DST) so conversions are deterministic across hosts.
    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static DateTime Utc0(int h, int m = 0) => new(2026, 7, 1, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void IsConfigured_requires_both_bounds()
    {
        UpdateWindow.IsConfigured(null, null).Should().BeFalse();
        UpdateWindow.IsConfigured(new TimeOnly(9, 0), null).Should().BeFalse();
        UpdateWindow.IsConfigured(new TimeOnly(9, 0), new TimeOnly(17, 0)).Should().BeTrue();
    }

    [Fact]
    public void IsWithin_is_true_for_any_time_when_no_window()
    {
        UpdateWindow.IsWithin(null, null, Utc, Utc0(3)).Should().BeTrue();
    }

    [Theory]
    [InlineData(12, true)]   // inside
    [InlineData(9, true)]    // start is inclusive
    [InlineData(17, false)]  // end is exclusive
    [InlineData(8, false)]   // before
    [InlineData(20, false)]  // after
    public void IsWithin_same_day_window(int hour, bool expected)
    {
        UpdateWindow.IsWithin(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, Utc0(hour)).Should().Be(expected);
    }

    [Theory]
    [InlineData(23, true)]   // after start, before midnight
    [InlineData(2, true)]    // after midnight, before end
    [InlineData(22, true)]   // start inclusive
    [InlineData(6, false)]   // end exclusive
    [InlineData(12, false)]  // middle of the day, outside
    public void IsWithin_wraps_past_midnight(int hour, bool expected)
    {
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Utc, Utc0(hour)).Should().Be(expected);
    }

    [Fact]
    public void NextOpeningUtc_returns_from_when_no_window()
    {
        var from = Utc0(3);
        UpdateWindow.NextOpeningUtc(null, null, Utc, from).Should().Be(from);
    }

    [Fact]
    public void NextOpeningUtc_returns_from_when_already_inside()
    {
        var from = Utc0(12);
        UpdateWindow.NextOpeningUtc(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, from).Should().Be(from);
    }

    [Fact]
    public void NextOpeningUtc_returns_today_start_when_before_it()
    {
        // 07:00, window 22:00–06:00 → next opening is 22:00 today.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(22, 0), new TimeOnly(6, 0), Utc, Utc0(7));
        next.Should().Be(Utc0(22));
    }

    [Fact]
    public void NextOpeningUtc_rolls_to_tomorrow_when_past_todays_start()
    {
        // 18:00, window 09:00–17:00 → today's window has closed, next opening 09:00 tomorrow.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(9, 0), new TimeOnly(17, 0), Utc, Utc0(18));
        next.Should().Be(new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void IsWithin_respects_the_timezone()
    {
        // 21:00 UTC = 23:00 in +02:00; window 22:00–06:00 local → inside.
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(21)).Should().BeTrue();
        // 19:00 UTC = 21:00 local → before the 22:00 start → outside.
        UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(19)).Should().BeFalse();
    }

    [Fact]
    public void NextOpeningUtc_converts_local_start_back_to_utc()
    {
        // +02:00 zone, window opens 22:00 local = 20:00 UTC. From 19:00 UTC (21:00 local,
        // before the local start) → next opening 20:00 UTC.
        var next = UpdateWindow.NextOpeningUtc(new TimeOnly(22, 0), new TimeOnly(6, 0), Plus2, Utc0(19));
        next.Should().Be(Utc0(20));
    }
}
