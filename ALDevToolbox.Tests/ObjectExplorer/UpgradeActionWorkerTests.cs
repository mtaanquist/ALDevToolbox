using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The worker that fires the slots the upgrade team booked (issue #657 Stage 4b). What is
/// pinned here: a due row actually reaches Business Central and is attributed to the
/// person who asked for it, a row that isn't due yet is left alone, a failure lands as a
/// failed row with a readable reason rather than taking the sweep down, and the
/// claim-versus-cancel race resolves the same way from both sides.
/// </summary>
public sealed class UpgradeActionWorkerTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task A_due_action_is_sent_and_attributed_to_the_person_who_booked_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        ran.Should().Be(1);
        _f.Admin.Writes.Should().Be(1);
        _f.Admin.SelectedIgnoreUpdateWindow.Should().BeTrue("a booked slot does exactly what pressing the button would have done");

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.SentAt.Should().Be(_f.Clock.GetUtcNow().UtcDateTime);
        stored.Outcome.Should().NotBeNullOrWhiteSpace();

        // The audit log has to name the person, not the machine: the worker runs under
        // the requester's identity precisely so this row reads like the immediate one.
        await using var verify = _f.Db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment && a.EntityId == envId)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        entry.ChangedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        entry.ChangedBy.Should().Contain("Anna Jensen");
    }

    [Fact]
    public async Task An_action_whose_slot_has_not_arrived_is_left_alone()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(1));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        ran.Should().Be(0);
        _f.Admin.Writes.Should().Be(0);
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Pending);
    }

    [Fact]
    public async Task A_live_refusal_at_fire_time_lands_as_a_failed_row_with_the_reason()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // Between booking and firing, the update was applied or withdrawn.
        _f.Admin.OnUpdates = Array.Empty<BcEnvironmentUpdate>;
        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("No update", "the feed says why in the words the service refused with");
        stored.SentAt.Should().NotBeNull("we tried, and when we tried is part of the history");
    }

    [Fact]
    public async Task One_customers_failure_does_not_stop_the_others()
    {
        var (projectA, envA) = await _f.SeedCustomerAsync("CRONUS Denmark");
        var (projectB, envB) = await _f.SeedCustomerAsync("CRONUS Norway", "Europe/Oslo");
        var failing = await BookAsync(projectA, envA, hoursAhead: 12);
        // An hour later, so the sweep's order is not in doubt.
        var fine = await BookAsync(projectB, envB, hoursAhead: 13);

        // The first customer's tenant refuses; every later read answers normally.
        var reads = 0;
        _f.Admin.OnUpdates = () => ++reads == 1
            ? throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "The credentials were rejected.")
            : new[] { UpgradeActionTestFixture.Update(UpgradeActionTestFixture.ScheduledDate, UpgradeActionTestFixture.LatestDate) };

        _f.Clock.Advance(TimeSpan.FromHours(14));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        ran.Should().Be(2, "one unreachable customer must not cost the other eighty-nine");
        (await _f.ReadActionAsync(failing)).Status.Should().Be(UpgradeActionStatus.Failed);
        (await _f.ReadActionAsync(fine)).Status.Should().Be(UpgradeActionStatus.Sent);
    }

    // ── The race, from both sides ───────────────────────────────────────

    [Fact]
    public async Task A_cancel_that_arrives_after_the_send_loses_cleanly()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        // A second context, exactly as a person's circuit would be: the cancel is refused
        // in words, not silently ignored.
        await using var cancelCtx = _f.Db.NewContext();
        var act = () => _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);
        (await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>())
            .Which.Errors.Values.Should().ContainMatch("*already run*");

        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Sent);
    }

    [Fact]
    public async Task A_send_that_arrives_after_the_cancel_never_reaches_Business_Central()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        await using (var cancelCtx = _f.Db.NewContext())
            await _f.Svc(cancelCtx).CancelUpgradeActionAsync(actionId);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        var ran = await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, CancellationToken.None);

        ran.Should().Be(0);
        _f.Admin.Writes.Should().Be(0, "the customer's tenant must never be touched by an action somebody took back");
        (await _f.ReadActionAsync(actionId)).Status.Should().Be(UpgradeActionStatus.Cancelled);
    }

    [Fact]
    public async Task An_action_a_restart_interrupted_mid_send_is_failed_rather_than_repeated()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var actionId = await BookAsync(projectId, envId, hoursAhead: 12);

        // The shape a crash leaves behind: claimed (sent_at stamped) but still pending.
        await using (var ctx = _f.Db.NewContext())
        {
            var row = await ctx.OeEnvironmentUpgradeActions.SingleAsync(a => a.Id == actionId);
            row.SentAt = _f.Clock.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();
        }

        await _f.Worker().FailInterruptedAsync(TestDb.DefaultOrgId, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Failed);
        stored.Outcome.Should().Contain("restarted");
        _f.Admin.Writes.Should().Be(0, "repeating 'start the update now' on a guess is not a safe default");
    }

    private async Task<int> BookAsync(int projectId, int environmentId, int hoursAhead)
    {
        await using var ctx = _f.Db.NewContext();
        var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, environmentId, UpgradeActionKind.RunNow,
            _f.Clock.GetUtcNow().AddHours(hoursAhead));
        return row.Id;
    }
}
