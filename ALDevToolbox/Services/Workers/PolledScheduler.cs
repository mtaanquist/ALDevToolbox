namespace ALDevToolbox.Services.Workers;

/// <summary>
/// Base for the time-driven background sweeps: wait out startup, then tick on a fixed
/// poll interval until shutdown. Polling (rather than one long <c>Task.Delay</c> to the
/// next due time) keeps the heartbeat fresh and means a slot missed over a restart is
/// picked up on the next poll — so a subclass's <see cref="TickAsync"/> decides for
/// itself whether this poll is its due window.
///
/// <para>
/// The base owns the startup delay, the poll loop, the heartbeat bracket, the
/// tick failure log, and the <c>DISABLE_*</c> opt-out: naming one in
/// <paramref name="disableEnvVar"/> is what makes it real, so a scheduler constructed
/// directly (in a test, or without the registration guard in <c>Program.cs</c>) honours
/// it too.
/// </para>
/// </summary>
public abstract class PolledScheduler : BackgroundService
{
    /// <summary>
    /// One convention for every scheduler: let startup migrations, the first-run seed
    /// and the bootstrap admin settle before opening another scope on the same
    /// connection pool.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger;
    private readonly string _name;
    private readonly string? _disableEnvVar;
    private readonly TimeSpan _pollInterval;

    protected WorkerHeartbeat Heartbeat { get; }

    /// <param name="pollInterval">How often <see cref="TickAsync"/> is offered a turn.</param>
    /// <param name="maxActiveDuration">The longest legitimate single tick.</param>
    /// <param name="maxIdleSilence">
    /// How long the loop may go without a tick before <c>/healthz/workers</c> calls it
    /// stalled — roughly 3x the poll interval.
    /// </param>
    /// <param name="disableEnvVar">Name of the <c>1</c>-valued env var that turns this sweep off.</param>
    protected PolledScheduler(
        ILogger logger,
        WorkerHeartbeatRegistry heartbeats,
        string name,
        TimeSpan pollInterval,
        TimeSpan maxActiveDuration,
        TimeSpan maxIdleSilence,
        string disableEnvVar)
    {
        _logger = logger;
        _name = name;
        _pollInterval = pollInterval;
        _disableEnvVar = disableEnvVar;
        Heartbeat = heartbeats.Register(name, maxActiveDuration, maxIdleSilence);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_disableEnvVar is not null && Environment.GetEnvironmentVariable(_disableEnvVar) == "1")
        {
            _logger.LogInformation("{Scheduler} disabled via {EnvVar}=1.", _name, _disableEnvVar);
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            Heartbeat.Tick();
            try
            {
                Heartbeat.BeginActive();
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                finally { Heartbeat.EndActive(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Scheduler} tick threw; will retry on the next poll.", _name);
            }

            try { await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One poll. Returns immediately when this poll is not the sweep's due window.
    /// Failures are logged and retried on the next poll, so a tick may throw.
    /// </summary>
    protected abstract Task TickAsync(CancellationToken ct);
}
