using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for the transactional-email outbox (issue #790): what a
/// queued message looks like, how a failed attempt backs off and eventually
/// gives up, and what the SiteAdmin list is allowed to see.
///
/// <para>The rule these all defend is that a failed send has to leave a trace.
/// Three flows (signup verification, forgot password, magic link) must answer
/// the user identically whether or not the mail went out, so the row in
/// <c>email_outbox</c> is the only thing that can tell an operator SMTP has
/// stopped working.</para>
/// </summary>
public sealed class EmailOutboxTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private EmailOutbox NewOutbox() =>
        new(_db.NewContextFactory(), _db.DataProtectionProvider, _clock, NullLogger<EmailOutbox>.Instance);

    private async Task<EmailOutboxMessage> RowAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.EmailOutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task<int> EnqueueOneAsync(
        EmailOutbox outbox, EmailPurpose purpose = EmailPurpose.PasswordReset, int? orgId = null)
    {
        await outbox.EnqueueAsync("user@cronus.com", "Reset your password", "<p>link</p>", purpose, orgId);
        await using var ctx = _db.NewContext();
        return await ctx.EmailOutboxMessages.OrderByDescending(m => m.Id).Select(m => m.Id).FirstAsync();
    }

    [Fact]
    public async Task Enqueue_writes_a_pending_row_that_is_due_immediately()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox, EmailPurpose.MagicLink, TestDb.DefaultOrgId);

        var row = await RowAsync(id);
        row.Status.Should().Be(EmailOutboxStatus.Pending);
        row.Purpose.Should().Be(EmailPurpose.MagicLink);
        row.ToEmail.Should().Be("user@cronus.com");
        row.AttemptCount.Should().Be(0);
        row.SentAt.Should().BeNull();
        row.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.NextAttemptAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Enqueue_stores_the_body_encrypted_and_reads_it_back()
    {
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(
            "user@cronus.com", "Reset your password", "<p>https://example.com/reset?token=secret</p>",
            EmailPurpose.PasswordReset, organizationId: null);

        var row = await RowAsync((await RowIdsAsync()).Single());
        // The body carries a working reset link, so the column must not be
        // readable by anything that can only read the database.
        row.BodyEncrypted.Should().NotBeNullOrEmpty();
        row.BodyEncrypted.Should().NotContain("secret");

        outbox.TryReadBody(row).Should().Be("<p>https://example.com/reset?token=secret</p>");
    }

    [Fact]
    public async Task TryReadBody_returns_null_when_the_ciphertext_cannot_be_read()
    {
        var outbox = NewOutbox();
        // What a replaced Data Protection key ring looks like from here.
        var unreadable = new EmailOutboxMessage { Id = 1, BodyEncrypted = "not-really-ciphertext" };
        outbox.TryReadBody(unreadable).Should().BeNull();
    }

    [Fact]
    public async Task Due_returns_only_messages_whose_next_attempt_has_arrived()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        (await outbox.DueAsync(10)).Should().ContainSingle(m => m.Id == id);

        await outbox.RecordFailureAsync(id, "Connection refused");
        (await outbox.DueAsync(10)).Should().BeEmpty("a failed attempt backs off before the next try");

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await outbox.DueAsync(10)).Should().ContainSingle(m => m.Id == id);
    }

    [Fact]
    public async Task A_failed_attempt_keeps_the_message_pending_and_records_the_error()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        await outbox.RecordFailureAsync(id, "535 authentication failed");

        var row = await RowAsync(id);
        row.Status.Should().Be(EmailOutboxStatus.Pending);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Be("535 authentication failed");
        row.NextAttemptAt.Should().BeAfter(_clock.GetUtcNow().UtcDateTime);
        row.BodyEncrypted.Should().NotBeNull("a message that will be retried still needs its body");
    }

    [Fact]
    public async Task The_message_is_given_up_on_once_the_attempts_run_out()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        for (var i = 0; i < EmailOutbox.MaxAttempts; i++)
        {
            await outbox.RecordFailureAsync(id, "Connection refused");
            _clock.Advance(TimeSpan.FromHours(2));
        }

        var row = await RowAsync(id);
        row.Status.Should().Be(EmailOutboxStatus.Failed);
        row.AttemptCount.Should().Be(EmailOutbox.MaxAttempts);
        row.LastError.Should().Be("Connection refused");
        // The token inside expired hours ago; keeping it would be exposure with
        // nothing left to gain. The row itself stays, because it is the evidence.
        row.BodyEncrypted.Should().BeNull();
        (await outbox.DueAsync(10)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_permanent_failure_gives_up_on_the_first_attempt()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        await outbox.RecordFailureAsync(id, "The message body could not be read", permanent: true);

        var row = await RowAsync(id);
        row.Status.Should().Be(EmailOutboxStatus.Failed);
        row.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task A_long_error_is_truncated_rather_than_stored_whole()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        await outbox.RecordFailureAsync(id, new string('x', 5000));

        var row = await RowAsync(id);
        row.LastError!.Length.Should().BeLessThan(1100);
    }

    [Fact]
    public async Task Marking_a_message_sent_stamps_it_and_drops_the_body()
    {
        var outbox = NewOutbox();
        var id = await EnqueueOneAsync(outbox);

        await outbox.MarkSentAsync(id);

        var row = await RowAsync(id);
        row.Status.Should().Be(EmailOutboxStatus.Sent);
        row.SentAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        row.AttemptCount.Should().Be(1);
        row.BodyEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task Pruning_clears_sent_messages_long_before_given_up_ones()
    {
        var outbox = NewOutbox();
        var sentId = await EnqueueOneAsync(outbox);
        var failedId = await EnqueueOneAsync(outbox, EmailPurpose.Invite);
        await outbox.MarkSentAsync(sentId);
        await outbox.RecordFailureAsync(failedId, "Connection refused", permanent: true);

        _clock.Advance(EmailOutbox.SentRetention + TimeSpan.FromMinutes(1));
        var (sent, failed) = await outbox.PruneAsync();
        sent.Should().Be(1);
        failed.Should().Be(0, "an operator still needs to see what never got through");

        _clock.Advance(EmailOutbox.FailedRetention);
        (await outbox.PruneAsync()).Failed.Should().Be(1);
    }

    [Fact]
    public async Task The_site_admin_snapshot_shows_what_failed_and_what_is_waiting()
    {
        var outbox = NewOutbox();
        var failedId = await EnqueueOneAsync(outbox, EmailPurpose.PasswordReset, TestDb.DefaultOrgId);
        var waitingId = await EnqueueOneAsync(outbox, EmailPurpose.Invite);
        var sentId = await EnqueueOneAsync(outbox, EmailPurpose.MagicLink);
        await outbox.RecordFailureAsync(failedId, "Connection refused", permanent: true);
        await outbox.MarkSentAsync(sentId);

        var snapshot = await outbox.SnapshotAsync();

        snapshot.Failed.Should().ContainSingle().Which.Id.Should().Be(failedId);
        snapshot.Failed[0].LastError.Should().Be("Connection refused");
        snapshot.Failed[0].OrganizationName.Should().NotBeNullOrEmpty();
        snapshot.Waiting.Should().ContainSingle().Which.Id.Should().Be(waitingId);
        snapshot.Waiting[0].OrganizationName.Should().BeNull("the pre-auth flows have no organisation yet");
        snapshot.SentLastDay.Should().Be(1);
    }

    private async Task<List<int>> RowIdsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.EmailOutboxMessages.OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
    }
}
