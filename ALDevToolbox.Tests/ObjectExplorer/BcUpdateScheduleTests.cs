using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The reading of the Admin Center's exclusive <c>latestSelectableDateTime</c> bound
/// (issue #804). Every date the Upgrades page shows and every date the push write sends
/// comes through here, so the rule is pinned on its own rather than only through a
/// service test.
/// </summary>
public sealed class BcUpdateScheduleTests
{
    [Fact]
    public void A_midnight_bound_is_a_day_boundary_so_the_last_date_we_may_ask_for_is_the_day_before()
    {
        var bound = new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

        BcUpdateSchedule.EffectiveLatest(bound)
            .Should().Be(new DateTimeOffset(2027, 2, 28, 0, 0, 0, TimeSpan.Zero),
                "'before 1 March' is what the admin center's own picker caps at 28 February");
    }

    [Fact]
    public void A_bound_with_a_time_of_day_is_already_a_real_moment_and_is_kept()
    {
        var bound = new DateTimeOffset(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

        BcUpdateSchedule.EffectiveLatest(bound).Should().Be(bound);
    }

    [Fact]
    public void A_bound_given_in_another_offset_is_read_in_utc_first()
    {
        // 2027-03-01T01:00+01:00 is midnight UTC: the same day boundary, written differently.
        var bound = new DateTimeOffset(2027, 3, 1, 1, 0, 0, TimeSpan.FromHours(1));

        BcUpdateSchedule.EffectiveLatest(bound)
            .Should().Be(new DateTimeOffset(2027, 2, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void An_update_with_no_bound_has_no_effective_latest()
    {
        BcUpdateSchedule.EffectiveLatest((DateTimeOffset?)null).Should().BeNull();
        BcUpdateSchedule.EffectiveLatestUtc((DateTime?)null).Should().BeNull();
    }

    [Fact]
    public void The_mirrored_column_reads_the_same_way()
    {
        BcUpdateSchedule.EffectiveLatestUtc(new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc));

        var moment = new DateTime(2026, 10, 12, 22, 0, 0, DateTimeKind.Utc);
        BcUpdateSchedule.EffectiveLatestUtc(moment).Should().Be(moment);
    }
}
