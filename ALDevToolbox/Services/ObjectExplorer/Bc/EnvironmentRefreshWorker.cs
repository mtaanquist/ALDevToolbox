using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Drains <see cref="EnvironmentRefreshQueue"/> and re-reads each project's Business
/// Central environments off the request thread, re-mirroring the next-platform-update
/// columns the fleet page lists.
///
/// <para>
/// One project at a time (the channel is single-reader). Each job runs in its own DI
/// scope under the job's <see cref="AmbientOrganizationScope"/> identity so the EF query
/// filter behaves exactly as it would in a request, and goes through the unattended
/// refresh path — a sweep nobody asked for must not present itself as the consultant's
/// own "Test connection" result. The in-flight flag is cleared in <c>finally</c>
/// regardless of outcome; a project whose credentials have gone stale simply logs and
/// leaves the previous mirror in place.
/// </para>
/// </summary>
public sealed class EnvironmentRefreshWorker : QueueDrainWorker<EnvironmentRefreshJob>
{
    private readonly EnvironmentRefreshQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<EnvironmentRefreshWorker> _logger;

    public EnvironmentRefreshWorker(
        EnvironmentRefreshQueue queue,
        IServiceProvider services,
        ILogger<EnvironmentRefreshWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // One project is a token call plus two admin-center calls per environment, so 15
        // minutes is ample even for a customer with many sandboxes while still catching a
        // wedged job.
        : base(queue.Reader, logger, heartbeats, nameof(EnvironmentRefreshWorker), TimeSpan.FromMinutes(15))
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task RunJobAsync(EnvironmentRefreshJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<ProjectConnectionService>();
        var result = await connections.RefreshEnvironmentsUnattendedAsync(job.ProjectId, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Refreshed Business Central environments for project {ProjectId}: {Count} environment(s).",
                job.ProjectId, result.EnvironmentCount);
        }
        else
        {
            // Not an error on our side: a customer's credentials expire, GDAP lapses. The
            // previous mirror and its age stay visible, which is what the page shows.
            _logger.LogWarning(
                "Couldn't refresh Business Central environments for project {ProjectId}: {Message}",
                job.ProjectId, result.Message);
        }
    }

    protected override void OnJobFinished(EnvironmentRefreshJob job) => _queue.Complete(job.ProjectId);

    protected override string Describe(EnvironmentRefreshJob job) => $"ProjectId={job.ProjectId}";
}
