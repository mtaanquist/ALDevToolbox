using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Domain.ValueObjects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Which date, if any, travels with a version change (issue #980). Business Central
/// refuses to select an update that already carries a past date, so the rule is pinned
/// here on its own, without a database; the service tests cover it end to end.
/// </summary>
public sealed class VersionChangeDateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Bound = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static BcEnvironmentUpdate Target(DateTimeOffset? date, DateTimeOffset? latest) =>
        new("29.2", true, false, "", "GA", date, latest, false, "Active", null, null);

    private static BcEnvironmentUpdate Current(DateTimeOffset? date) =>
        new("28.5", true, true, "scheduled", "GA", date, null, false, "Active", null, null);

    [Fact]
    public void A_target_with_no_date_sends_none()
    {
        ProjectConnectionService.DateForVersionChange(Target(null, Bound), Current(Now.AddDays(5)), Now)
            .Should().BeNull();
    }

    [Fact]
    public void A_target_with_a_future_date_sends_none()
    {
        ProjectConnectionService.DateForVersionChange(Target(Now.AddDays(3), Bound), Current(Now.AddDays(5)), Now)
            .Should().BeNull("Business Central selects an update with a future date without complaint");
    }

    [Fact]
    public void A_past_target_date_keeps_the_current_slot_when_it_is_ahead_and_inside_the_bound()
    {
        var agreed = Now.AddDays(5);

        ProjectConnectionService.DateForVersionChange(Target(new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), Bound), Current(agreed), Now)
            .Should().Be(agreed);
    }

    [Fact]
    public void A_past_target_date_falls_back_to_the_latest_allowed_when_the_current_slot_has_passed()
    {
        ProjectConnectionService.DateForVersionChange(Target(Now.AddDays(-2), Bound), Current(Now.AddDays(-1)), Now)
            .Should().Be(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero),
                "the midnight bound is exclusive, so the last day allowed is the one before");
    }

    [Fact]
    public void A_past_target_date_falls_back_to_the_latest_allowed_when_the_current_slot_is_beyond_the_bound()
    {
        ProjectConnectionService.DateForVersionChange(Target(Now.AddDays(-2), Bound), Current(Bound.AddDays(10)), Now)
            .Should().Be(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_past_target_date_with_no_current_update_sends_the_latest_allowed()
    {
        ProjectConnectionService.DateForVersionChange(Target(Now.AddDays(-2), Bound), null, Now)
            .Should().Be(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_target_date_only_seconds_ahead_counts_as_past()
    {
        // It will have passed by the time the write reaches Business Central.
        ProjectConnectionService.DateForVersionChange(Target(Now.AddSeconds(30), Bound), Current(Now.AddSeconds(40)), Now)
            .Should().Be(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero),
                "the current slot is inside the same grace window, so it is not safe to send either");
    }

    [Fact]
    public void A_past_target_date_with_no_bound_is_refused_on_the_version_field()
    {
        var act = () => ProjectConnectionService.DateForVersionChange(Target(Now.AddDays(-2), null), Current(Now.AddDays(5)), Now);

        var error = act.Should().Throw<PlanValidationException>().Which.Errors["TargetVersion"];
        error.Should().Contain("admin centre");
        error.Should().NotContainAny("selectedDateTime", "scheduleDetails", "EntityValidationFailed");
    }

    [Fact]
    public void A_past_target_date_whose_bound_has_also_passed_is_refused()
    {
        var act = () => ProjectConnectionService.DateForVersionChange(
            Target(Now.AddDays(-2), new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero)), Current(null), Now);

        act.Should().Throw<PlanValidationException>().Which.Errors.Should().ContainKey("TargetVersion");
    }
}
