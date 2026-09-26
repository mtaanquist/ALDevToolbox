using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The derived state of a planned upgrade's line, and of the upgrade itself (issue #984).
/// One test per rule, then one per priority collision, because the order the rules run in
/// is the whole of the design: a tick beats the mirror, a running update beats a version
/// that already looks new, and so on.
/// </summary>
public sealed class EnvironmentUpgradeLineStateTests
{
    private const string Target = "28.5";
    private static readonly DateTime T0 = new(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);

    private static UpgradeFleetRow Row(string? status = "Active", string? version = "27.5.12345.0", string? nextStatus = "Scheduled") =>
        new(1, "CRONUS Denmark", "Europe/Copenhagen", 2, "Production", "Production", status, version,
            "28.5", "GA", nextStatus, T0.AddDays(2), T0.AddDays(20), false, T0, true);

    private static int _id;

    private static OeEnvironmentUpgradeAction Action(
        UpgradeActionKind kind, UpgradeActionStatus status, int minutes = 0)
    {
        var at = T0.AddMinutes(minutes);
        return new OeEnvironmentUpgradeAction
        {
            Id = ++_id,
            Kind = kind,
            Status = status,
            RequestedAt = at,
            ExecuteAfter = at,
            SentAt = status is UpgradeActionStatus.Sent or UpgradeActionStatus.Failed ? at : null,
        };
    }

    private static UpgradeLineState Derive(UpgradeFleetRow row, bool isChecked = false, params OeEnvironmentUpgradeAction[] actions) =>
        EnvironmentUpgradeLineState.Derive(row, actions, isChecked, Target);

    // ── One rule at a time ──────────────────────────────────────────────

    [Fact]
    public void Nothing_done_yet_is_planned() =>
        Derive(Row()).Should().Be(UpgradeLineState.Planned);

    [Fact]
    public void A_ticked_check_is_checked() =>
        Derive(Row(), isChecked: true).Should().Be(UpgradeLineState.Checked);

    [Theory]
    [InlineData("Upgrading", "Scheduled")]
    [InlineData("Active", "Running")]
    public void Business_Central_busy_with_the_update_is_running(string status, string nextStatus) =>
        Derive(Row(status: status, nextStatus: nextStatus)).Should().Be(UpgradeLineState.Running);

    [Theory]
    [InlineData("28.5.30000.0")]
    [InlineData("28.6.1.0")]
    [InlineData("29.0.1.0")]
    public void On_the_target_release_or_later_is_updated(string version) =>
        Derive(Row(version: version)).Should().Be(UpgradeLineState.Updated);

    [Fact]
    public void Versions_compare_by_number_not_by_text()
    {
        // "28.10" sorts before "28.5" as text; it is the later release.
        EnvironmentUpgradeLineState.Derive(Row(version: "28.10.1.0"), [], false, "28.9")
            .Should().Be(UpgradeLineState.Updated);
    }

    [Fact]
    public void The_latest_action_failing_is_failed() =>
        Derive(Row(), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Failed))
            .Should().Be(UpgradeLineState.Failed);

    [Fact]
    public void Business_Central_reporting_a_failed_state_is_failed() =>
        Derive(Row(status: "UpgradingFailed")).Should().Be(UpgradeLineState.Failed);

    [Fact]
    public void A_start_waiting_for_its_slot_is_booked() =>
        Derive(Row(), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Pending))
            .Should().Be(UpgradeLineState.Booked);

    [Fact]
    public void A_start_Business_Central_accepted_but_has_not_picked_up_is_booked() =>
        Derive(Row(), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Sent))
            .Should().Be(UpgradeLineState.Booked);

    [Fact]
    public void A_date_moved_to_the_latest_is_date_moved() =>
        Derive(Row(), false, Action(UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Sent))
            .Should().Be(UpgradeLineState.DateMoved);

    [Fact]
    public void A_cancelled_booking_falls_back_to_planned() =>
        Derive(Row(), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Cancelled))
            .Should().Be(UpgradeLineState.Planned);

    [Fact]
    public void A_cancelled_booking_after_a_date_move_reads_as_the_date_move() =>
        Derive(Row(), false,
                Action(UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Sent, 0),
                Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Cancelled, 10))
            .Should().Be(UpgradeLineState.DateMoved);

    [Fact]
    public void A_version_change_alone_is_still_planned() =>
        Derive(Row(), false, Action(UpgradeActionKind.SelectVersion, UpgradeActionStatus.Sent))
            .Should().Be(UpgradeLineState.Planned);

    // ── Priority collisions ─────────────────────────────────────────────

    [Fact]
    public void Checked_beats_running() =>
        Derive(Row(status: "Upgrading"), isChecked: true).Should().Be(UpgradeLineState.Checked);

    [Fact]
    public void Checked_beats_failed() =>
        Derive(Row(status: "UpgradingFailed"), true, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Failed))
            .Should().Be(UpgradeLineState.Checked);

    [Fact]
    public void Running_beats_updated() =>
        Derive(Row(status: "Upgrading", version: "28.5.1.0")).Should().Be(UpgradeLineState.Running);

    [Fact]
    public void Running_beats_booked() =>
        Derive(Row(status: "Upgrading"), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Sent))
            .Should().Be(UpgradeLineState.Running);

    [Fact]
    public void Updated_beats_a_failed_action() =>
        Derive(Row(version: "28.5.1.0"), false, Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Failed))
            .Should().Be(UpgradeLineState.Updated);

    [Fact]
    public void Failed_beats_an_older_booking() =>
        Derive(Row(), false,
                Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Pending, 0),
                Action(UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Failed, 5))
            .Should().Be(UpgradeLineState.Failed);

    [Fact]
    public void A_later_booking_supersedes_an_earlier_failure() =>
        Derive(Row(), false,
                Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Failed, 0),
                Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Pending, 5))
            .Should().Be(UpgradeLineState.Booked);

    [Fact]
    public void Booked_beats_date_moved() =>
        Derive(Row(), false,
                Action(UpgradeActionKind.PushDateToLatest, UpgradeActionStatus.Sent, 0),
                Action(UpgradeActionKind.RunNow, UpgradeActionStatus.Pending, 5))
            .Should().Be(UpgradeLineState.Booked);

    // ── The header ──────────────────────────────────────────────────────

    [Fact]
    public void A_closed_upgrade_is_done_whatever_its_lines_say() =>
        EnvironmentUpgradeLineState.DeriveStatus(true, [UpgradeLineState.Running, UpgradeLineState.Failed])
            .Should().Be(UpgradeStatus.Done);

    [Fact]
    public void An_upgrade_with_no_lines_is_planned() =>
        EnvironmentUpgradeLineState.DeriveStatus(false, []).Should().Be(UpgradeStatus.Planned);

    [Fact]
    public void Nothing_beyond_a_moved_date_is_planned() =>
        EnvironmentUpgradeLineState.DeriveStatus(false, [UpgradeLineState.Planned, UpgradeLineState.DateMoved])
            .Should().Be(UpgradeStatus.Planned);

    [Theory]
    [InlineData(UpgradeLineState.Booked)]
    [InlineData(UpgradeLineState.Running)]
    public void Anything_booked_or_running_is_in_progress(UpgradeLineState busy) =>
        EnvironmentUpgradeLineState.DeriveStatus(false, [UpgradeLineState.Updated, UpgradeLineState.Checked, busy])
            .Should().Be(UpgradeStatus.InProgress);

    [Fact]
    public void Every_line_answered_is_updated() =>
        EnvironmentUpgradeLineState.DeriveStatus(false,
                [UpgradeLineState.Updated, UpgradeLineState.Failed, UpgradeLineState.Checked])
            .Should().Be(UpgradeStatus.Updated);

    [Fact]
    public void A_wave_part_way_through_is_in_progress() =>
        EnvironmentUpgradeLineState.DeriveStatus(false, [UpgradeLineState.Updated, UpgradeLineState.Planned])
            .Should().Be(UpgradeStatus.InProgress);

    [Theory]
    [InlineData(UpgradeLineState.Failed, true)]
    [InlineData(UpgradeLineState.Planned, true)]
    [InlineData(UpgradeLineState.DateMoved, true)]
    [InlineData(UpgradeLineState.Booked, false)]
    [InlineData(UpgradeLineState.Running, false)]
    [InlineData(UpgradeLineState.Updated, false)]
    [InlineData(UpgradeLineState.Checked, false)]
    public void Leftovers_are_the_failed_and_the_never_started(UpgradeLineState state, bool leftover) =>
        EnvironmentUpgradeLineState.IsLeftover(state).Should().Be(leftover);
}
