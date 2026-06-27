using ALDevToolbox.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Drains <see cref="ProjectDiscoveryQueue"/> and warms each project's
/// discovered-extensions cache off the request thread, so the pipeline editor's
/// checklist appears instantly from cache instead of cloning on every open.
/// Mirrors <see cref="ReleaseImportWorker"/> but minimal — discovery only writes a
/// denormalised cache on the project row, and a failure leaves the prior good list
/// intact.
///
/// <para>
/// One project at a time (the channel is single-reader). Each job runs in its own
/// DI scope under the requesting user's <see cref="AmbientOrganizationScope"/>
/// identity so the EF query filter and the repo-token lookup behave exactly as
/// they would in the original request. The in-flight flag is cleared in
/// <c>finally</c> regardless of outcome.
/// </para>
/// </summary>
public sealed class ProjectDiscoveryWorker : BackgroundService
{
    private readonly ProjectDiscoveryQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<ProjectDiscoveryWorker> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    public ProjectDiscoveryWorker(
        ProjectDiscoveryQueue queue,
        IServiceProvider services,
        ILogger<ProjectDiscoveryWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        // Queue-driven: sits idle until an editor refreshes, so no idle-silence
        // ceiling (null). A single discovery clones only trees + app.json blobs and
        // is bounded by DiscoveryCloneTimeout (3 min) per repo; 15 minutes leaves
        // ample margin for a many-repo project while still catching a wedged job.
        _heartbeat = heartbeats.Register(nameof(ProjectDiscoveryWorker),
            maxActiveDuration: TimeSpan.FromMinutes(15),
            maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _heartbeat.BeginActive();
            try
            {
                await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RunJobAsync captures its own failures into the cache; this is the
                // last-resort net so one bad job never kills the worker loop.
                _logger.LogError(ex, "Project discovery worker tripped on ProjectId={ProjectId}.", job.ProjectId);
            }
            finally
            {
                _queue.Complete(job.ProjectId);
                _heartbeat.EndActive();
            }
        }
    }

    private async Task RunJobAsync(ProjectDiscoveryJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var buildService = scope.ServiceProvider.GetRequiredService<ProjectBuildService>();
        await buildService.DiscoverExtensionsForCacheAsync(job.ProjectId, ct).ConfigureAwait(false);
    }
}
