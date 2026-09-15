using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services;

/// <summary>
/// Drains <see cref="EmailOutbox"/>: sends what is due, backs off what fails,
/// and prunes what is past its retention. Issue #790.
///
/// <para>
/// A table sweep rather than a channel, because the work has to survive a
/// deploy — an unsent password reset that a restart dropped would put us back
/// where we started. The poll interval is the worst-case delay a queued message
/// sees, so it is short; the messages that cannot afford even that are sent
/// inline and never reach this loop (see <see cref="EmailPurposes.SendsInline"/>).
/// Opt out with <c>DISABLE_EMAIL_OUTBOX_SCHEDULER=1</c>.
/// </para>
///
/// <para>
/// The drain assumes it is the only one running, which holds for the one-app-container
/// deployment in <c>.design/architecture.md</c>: rows are taken by a plain read
/// rather than claimed, so a second instance would send some messages twice.
/// Give it a claim step before running two.
/// </para>
/// </summary>
public sealed class EmailOutboxScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the retention delete runs. The drain runs every poll.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>Messages one sweep will try. The rest wait for the next poll.</summary>
    private const int BatchSize = 25;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailOutboxScheduler> _logger;
    private DateTimeOffset? _lastPruneUtc;

    public EmailOutboxScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<EmailOutboxScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(EmailOutboxScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(3),
            disableEnvVar: "DISABLE_EMAIL_OUTBOX_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutbox>();
        var smtp = scope.ServiceProvider.GetRequiredService<SmtpEmailService>();

        foreach (var message in await outbox.DueAsync(BatchSize, ct))
        {
            ct.ThrowIfCancellationRequested();
            var body = outbox.TryReadBody(message);
            if (body is null)
            {
                // Unreadable ciphertext, or a body already dropped. Neither gets
                // better by trying again.
                await outbox.RecordFailureAsync(
                    message.Id,
                    "The message body could not be read, so it can never be sent. This usually means the Data Protection key ring was replaced.",
                    permanent: true,
                    ct);
                continue;
            }

            try
            {
                await smtp.SendAsync(message.ToEmail, message.Subject, body, message.Purpose, ct);
                // Delivery is at-least-once: if the send lands and this update
                // does not, the row stays pending and goes out twice. A second
                // reset link is a smaller harm than a first one never arriving,
                // which is the trade the other order would make.
                await outbox.MarkSentAsync(message.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown mid-sweep: leave the row pending for the next start.
                throw;
            }
            catch (Exception ex)
            {
                await outbox.RecordFailureAsync(message.Id, ex.Message, permanent: false, ct);
            }
        }

        await PruneIfDueAsync(outbox, ct);
    }

    private async Task PruneIfDueAsync(EmailOutbox outbox, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastPruneUtc is { } last && now - last < PruneInterval) return;

        var (sent, failed) = await outbox.PruneAsync(ct);
        _lastPruneUtc = now;
        if (sent > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Pruned {Sent} sent and {Failed} given-up email(s) from the outbox.", sent, failed);
        }
    }
}
