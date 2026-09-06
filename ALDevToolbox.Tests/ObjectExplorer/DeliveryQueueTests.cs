using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The in-memory dedupe / in-flight contract of <see cref="DeliveryQueue"/> —
/// the same shape <see cref="ProjectDiscoveryQueueTests"/> pins for its sibling,
/// but with sharper stakes: the dedupe is what a double-click on "Release this
/// build now" hits, and losing it publishes the same build twice to a live BC
/// environment. Losing <see cref="DeliveryQueue.Complete"/> in the worker's
/// finally wedges that delivery id forever.
/// </summary>
public sealed class DeliveryQueueTests
{
    private static DeliveryJob Job(int deliveryId) =>
        new(deliveryId, new AmbientOrganizationScope.OrganizationIdentity(
            TestDb.DefaultOrgId, UserId: 1, IsSiteAdmin: false, IsSystemOrganization: false));

    [Fact]
    public async Task Enqueue_marks_the_delivery_in_flight()
    {
        var queue = new DeliveryQueue();

        var enqueued = await queue.EnqueueAsync(Job(42));

        enqueued.Should().BeTrue();
        // The queue exposes no IsInFlight probe; a second enqueue is the observable
        // form of the flag, and it is the behaviour that matters.
        (await queue.EnqueueAsync(Job(42))).Should().BeFalse();
        (await queue.EnqueueAsync(Job(99))).Should().BeTrue("only the enqueued delivery is in flight");
    }

    [Fact]
    public async Task Enqueue_dedupes_an_already_in_flight_delivery()
    {
        var queue = new DeliveryQueue();

        (await queue.EnqueueAsync(Job(42))).Should().BeTrue();
        (await queue.EnqueueAsync(Job(42))).Should().BeFalse("a delivery is already queued/running for this id");

        // The first job is still the only one on the channel — the duplicate was dropped.
        queue.Reader.TryRead(out var first).Should().BeTrue();
        first!.DeliveryId.Should().Be(42);
        queue.Reader.TryRead(out _).Should().BeFalse("the duplicate enqueue wrote nothing");
    }

    [Fact]
    public async Task Complete_clears_the_flag_and_allows_re_enqueue()
    {
        var queue = new DeliveryQueue();
        await queue.EnqueueAsync(Job(42));

        queue.Complete(42);

        (await queue.EnqueueAsync(Job(42))).Should().BeTrue("once finished, the delivery can be re-run");
    }

    /// <summary>
    /// An exception escaping a job must not tear the worker down: the default
    /// <c>BackgroundServiceExceptionBehavior</c> is StopHost, so one bad publish
    /// would take the app with it — and the missed <c>Complete</c> would wedge
    /// the delivery id. Resolving <see cref="DeliveryService"/> from a provider
    /// that has none throws exactly where a stubbed service throwing would, so
    /// the first of two jobs fails and the second must still be drained.
    /// </summary>
    [Fact]
    public async Task Worker_survives_a_failing_delivery_and_drains_the_next_one()
    {
        var queue = new DeliveryQueue();
        // Empty provider: GetRequiredService<DeliveryService>() throws for every job.
        var services = new ServiceCollection().BuildServiceProvider();
        var logger = new RecordingLogger();
        var worker = new DeliveryWorker(queue, services, logger, new WorkerHeartbeatRegistry());

        (await queue.EnqueueAsync(Job(1))).Should().BeTrue();
        (await queue.EnqueueAsync(Job(2))).Should().BeTrue();

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && logger.Errors.Count < 2)
        {
            await Task.Delay(20);
        }
        await worker.StopAsync(CancellationToken.None);

        // The second logged failure is the proof the loop was not torn down by the first.
        logger.Errors.Should().HaveCount(2);
        logger.Errors.Should().AllSatisfy(m => m.Should().Contain("DeliveryId"));

        // ...and the finally cleared both flags, so neither delivery id is wedged.
        (await queue.EnqueueAsync(Job(1))).Should().BeTrue("a failed delivery must be re-runnable");
        (await queue.EnqueueAsync(Job(2))).Should().BeTrue();
    }

    /// <summary>Captures the worker's error log so a drained-past-a-failure can be awaited deterministically.</summary>
    private sealed class RecordingLogger : ILogger<DeliveryWorker>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            lock (Errors) Errors.Add(formatter(state, exception));
        }
    }
}
