namespace ALDevToolbox.Startup;

/// <summary>
/// Object Explorer and everything filed under it: release import, projects and
/// pipelines, project builds, Business Central SaaS delivery and upgrades, and
/// the source/reference read side.
/// </summary>
public static class ObjectExplorerRegistration
{
    /// <summary>Registers the Object Explorer services, queues, workers and HTTP clients.</summary>
    public static IServiceCollection AddObjectExplorer(this IServiceCollection services)
    {
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.TranslationImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.CallSiteReferenceEmitter>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.CalImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.DvdDownloadService>();
        // Resolve + download Microsoft OnPrem artifacts straight from the CDN, and the
        // coordinator both the Artifacts tab and the auto-import scheduler call.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.BcArtifactService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ArtifactReleaseImporter>();
        // In-process hand-off + worker for the DVD-scale imports (folder-ZIP upload,
        // URL download) so the admin isn't held on the page while they ingest.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.ReleaseImportQueue>();
        // Owns the on-disk AL compiler volume; singleton so its provisioning gate is shared.
        // AL_COMPILER_* read once here rather than in the provisioner's
        // constructor; see #733 and Services/Configuration/.
        services.AddSingleton(sp => ALDevToolbox.Services.Configuration.AlCompilerOptions
            .FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.AlCompilerProvisioner>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.PersistedImportJobs>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.ReleaseImportWorker>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseManagementService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.PipelineService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleasePipelineService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        // Business Central SaaS delivery: connection config + the API seams. The token
        // cache is a singleton (shared in-memory bearer cache, like the compiler gate);
        // the clients are thin HTTP seams; the connection service is request-scoped.
        // See .design/saas-delivery.md.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        // Singleton so a panel read is shared across requests rather than per-request. See
        // BcPanelCache for why the window is short and why the caller must gate access first.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient, ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient, ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        // The delivery worker only needs a token from the connection service; expose that
        // narrow seam so the publish orchestration is testable without the OAuth round-trip.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IDeliveryTokenSource>(
            sp => sp.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>());
        // SaaS delivery (manual publish): the create/run orchestration is scoped; the queue is
        // a singleton hand-off to the hosted worker, mirroring ProjectDiscoveryQueue/Worker. The
        // worker runs the upload→install→poll publish off the request thread. No external queue.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.DeliveryService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.DeliveryQueue>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.DeliveryWorker>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ArtifactService>();
        // Project-build pipeline: the compile/ingest service, its release coordinator,
        // and the (stateless) external-process seam for git + alc.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectBuildService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectBuildImporter>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.IProcessRunner, ALDevToolbox.Services.ObjectExplorer.ProcessRunner>();
        // Background warm of the per-project discovered-extensions cache (the pipeline
        // editor's checklist): an in-process queue + worker, mirroring the release-import
        // pair. In-memory dedupe, no external dependency.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.ProjectDiscoveryQueue>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectDiscoveryService>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.ProjectDiscoveryWorker>();
        // Background re-read of a project's BC environments, which re-mirrors the next
        // platform update per environment. Same in-process queue + worker shape; fed by the
        // nightly sweep (see BackgroundWorkerRegistration) and by an on-demand refresh.
        // See .design/saas-delivery.md.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshQueue>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshWorker>();
        // The read side of the Upgrades page: the cross-project fleet list, plus the on-demand
        // hand-off into the refresh queue above. Request-scoped like every project-scoped read.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeFleetService>();
        // The write side: the upgrade actions the team asks for now or books for an agreed slot,
        // their per-environment activity feed, and the worker that fires the booked ones. The
        // worker polls the table rather than a channel, so a slot survives a restart.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionService>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionWorker>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.TranslationQueryService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReleaseComparisonService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ObjectSearchService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceQueryService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.SourceViewerService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceResolver>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ReferenceSessionService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.ObjectExplorerLinks>();
        // Business Central delivery client (token + Admin Center + automation APIs).
        // Fixed public Microsoft hosts (login.microsoftonline.com,
        // api.businesscentral.dynamics.com), so no SSRF guard is needed — just a bounded
        // timeout. The per-request bearer + URL are set by the BC clients. See
        // .design/saas-delivery.md.
        services.AddHttpClient(ALDevToolbox.Services.ObjectExplorer.Bc.BcConstants.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(100);
        });

        // DVD download client for the Object Explorer "import release from URL" flow.
        // Same SSRF guard as the CIMD client (dial only publicly routable IPs), but
        // redirects are allowed: Microsoft download URLs commonly 302 to a CDN, and the
        // ConnectCallback re-checks every hop's IP so a redirect still can't reach an
        // internal target. The long timeout covers the multi-GB body.
        services.AddHttpClient(nameof(ALDevToolbox.Services.ObjectExplorer.DvdDownloadService), client =>
            {
                client.Timeout = TimeSpan.FromMinutes(20);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                ConnectCallback = ALDevToolbox.Services.OAuth.SsrfGuard.ConnectAsync,
                // SocketsHttpHandler defaults PooledConnectionLifetime to infinite, so
                // a cached TCP connection that's silently gone half-dead (CDN edge
                // dropped state, NAT timeout, …) gets reused for the next request and
                // stalls mid-body. Recycle after 2 minutes so DVD imports — which are
                // far apart and intolerant of stale state — always dial a fresh
                // connection. This is the documented best-practice setting; see
                // https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines.
                // The range-resume retry in CopyWithRetriesAsync also leans on this:
                // when a body stalls, the retry's new SendAsync gets a fresh
                // connection rather than the same stuck pipe.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
        return services;
    }
}
