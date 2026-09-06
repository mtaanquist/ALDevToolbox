using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Turns a verified <c>pull_request</c> delivery into builds: works out which
/// organisation the installation belongs to, which of its solutions track the
/// repository, opens a check run per solution and queues a build behind each.
///
/// <para><strong>The organisation is found by looking, not by querying across
/// tenants.</strong> An installation id maps to exactly one organisation's
/// settings row, and a single cross-org read would answer it in one query - but
/// that read would be an <c>IgnoreQueryFilters()</c> call site inside code an
/// anonymous inbound request reaches, which is precisely the fence CLAUDE.md
/// asks about first. So this walks <c>organizations</c> (a table with no tenant
/// filter), enters an <see cref="AmbientOrganizationScope"/> per organisation and
/// asks each one, under its own filter, what installation it connected - the same
/// shape <c>EnvironmentRefreshScheduler</c> uses. It stops at the first match, and
/// deployments have tens of organisations rather than thousands.</para>
///
/// <para>The actual clone, compile and ingest is the ordinary project build,
/// entered through <see cref="ProjectBuildImporter.StartPullRequestBuildAsync"/>
/// and run by <see cref="ReleaseImportWorker"/>. This worker's whole job is the
/// routing. See <c>.design/github-integration-phase2.md</c> (#627).</para>
/// </summary>
public sealed class GitHubPullRequestBuildWorker : QueueDrainWorker<GitHubPullRequestJob>
{
    private readonly GitHubWebhookQueue _queue;
    private readonly IServiceProvider _services;
    private readonly MaintenanceModeState _maintenance;
    private readonly ILogger<GitHubPullRequestBuildWorker> _logger;

    /// <summary>
    /// How long a delivery waits before being offered again while a restore is in
    /// flight. Settable for tests, which cannot afford to wait half a minute.
    /// </summary>
    internal TimeSpan MaintenanceRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public GitHubPullRequestBuildWorker(
        GitHubWebhookQueue queue,
        IServiceProvider services,
        MaintenanceModeState maintenance,
        ILogger<GitHubPullRequestBuildWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // Routing only - the build itself is the release-import worker's active
        // duration, not this one's. Five minutes is generous for "ask a few orgs
        // what they connected, then open a check run".
        : base(queue.Reader, logger, heartbeats, nameof(GitHubPullRequestBuildWorker), TimeSpan.FromMinutes(5))
    {
        _queue = queue;
        _services = services;
        _maintenance = maintenance;
        _logger = logger;
    }

    /// <summary>Runs one job as the drain loop would. Test seam.</summary>
    internal Task RunOneAsync(GitHubPullRequestJob job, CancellationToken ct) => RunJobAsync(job, ct);

    protected override string Describe(GitHubPullRequestJob job) =>
        $"{job.RepositoryFullName}#{job.PullRequestNumber}@{job.HeadSha}";

    protected override async Task RunJobAsync(GitHubPullRequestJob job, CancellationToken ct)
    {
        // A restore is rewriting the database underneath us. The delivery was
        // accepted (the webhook route stays open through maintenance on purpose,
        // because GitHub disables a hook whose deliveries keep failing), but the
        // build behind it reads and writes tables that are being replaced. So the
        // job goes back on the queue rather than into the database.
        if (_maintenance.IsActive)
        {
            _logger.LogInformation(
                "Holding a pull-request build for {Job}: maintenance mode is active ({Reason}).",
                Describe(job), _maintenance.Reason);
            await Task.Delay(MaintenanceRetryDelay, ct).ConfigureAwait(false);
            if (!_queue.TryEnqueue(job))
            {
                _logger.LogWarning(
                    "Dropped a pull-request build for {Job}: the queue was full while maintenance mode was active.",
                    Describe(job));
            }
            return;
        }

        // Superseded before we even reached it: a newer push to the same pull
        // request arrived while this one waited. Building it would spend a
        // compile on a commit no reviewer is looking at, and would then complete
        // the check run for the wrong head.
        if (!_queue.IsLatest(job.Key, job.HeadSha))
        {
            _logger.LogInformation(
                "Skipping a pull-request build for {Job}: a newer commit was pushed before it started.", Describe(job));
            return;
        }

        var resolved = await ResolveOrganizationAsync(job.InstallationId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            // Ordinary rather than alarming: a GitHub organisation can have the
            // app installed without any toolbox organisation having connected it,
            // and a disconnected one keeps its webhook until somebody removes the
            // installation on GitHub.
            _logger.LogInformation(
                "Dropped a pull-request delivery for {Repository}: no organisation on this deployment has connected installation {InstallationId}.",
                job.RepositoryFullName, job.InstallationId);
            return;
        }

        var (identity, orgLogin) = resolved.Value;
        using var orgScope = AmbientOrganizationScope.Enter(identity);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A pull request from a member's own fork is built, and this is where
        // "member" stops being GitHub's word from the delivery and becomes an
        // answer we asked for ourselves. The delivery's author_association is
        // stamped when the pull request is opened and re-used by every later
        // push, so somebody who has left the organisation in the meantime still
        // arrives labelled MEMBER. Nothing is cloned until GitHub confirms the
        // membership now, on the installation token.
        if (job.IsMemberFork && !await AuthorIsStillAMemberAsync(job, orgLogin, scope, ct).ConfigureAwait(false))
        {
            // Dropped before any check run is opened: there is nothing to leave
            // spinning, and a pull request whose author we cannot vouch for is
            // one the toolbox says nothing about at all.
            return;
        }

        // Which solutions track this repository, under the organisation's own
        // query filter. Matching is on the normalised clone URL, because the same
        // repository is entered by hand with and without the .git suffix and with
        // either case.
        var candidates = await db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == RepositoryProvider.GitHub && r.Project!.DeletedAt == null)
            .Select(r => new { r.Id, r.Url, r.ProjectId, ProjectName = r.Project!.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = NormaliseRepositoryUrl(job.CloneUrl);
        var matches = candidates
            .Where(r => NormaliseRepositoryUrl(r.Url) == wanted)
            // One solution, one build, even when it lists the repository twice.
            .GroupBy(r => r.ProjectId)
            .Select(g => g.First())
            .ToList();

        if (matches.Count == 0)
        {
            _logger.LogInformation(
                "Dropped a pull-request delivery for {Repository}: no solution in organisation {OrganizationId} tracks it.",
                job.RepositoryFullName, identity.OrganizationId);
            return;
        }

        var checks = scope.ServiceProvider.GetRequiredService<GitHubCheckRunService>();
        var importer = scope.ServiceProvider.GetRequiredService<ProjectBuildImporter>();

        foreach (var match in matches)
        {
            long? checkRunId = null;
            try
            {
                checkRunId = await checks.OpenAsync(
                    job.InstallationId, job.RepositoryFullName, match.ProjectName, job.HeadSha, match.ProjectId, ct)
                    .ConfigureAwait(false);

                await importer.StartPullRequestBuildAsync(
                    projectId: match.ProjectId,
                    repositoryId: match.Id,
                    repositoryFullName: job.RepositoryFullName,
                    installationId: job.InstallationId,
                    headSha: job.HeadSha,
                    headRef: job.HeadRef,
                    pullRequestNumber: job.PullRequestNumber,
                    checkRunId: checkRunId,
                    forkAuthor: job.IsMemberFork ? job.AuthorLogin : null,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One solution's failure is not the others'. The pull request
                // simply gets one fewer answer, and the reason is here.
                _logger.LogError(ex,
                    "Could not start a pull-request build of solution {ProjectId} for {Job}.", match.ProjectId, Describe(job));

                // The run was opened before the build was queued, so a failure
                // between the two would leave it spinning on the pull request
                // until somebody pushed again. Close it instead, saying why.
                if (checkRunId is long openRun)
                {
                    await checks.AbandonAsync(
                        job.InstallationId, job.RepositoryFullName, openRun,
                        "The toolbox could not start this build. Push again, or look at the solution in the toolbox.",
                        ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Whether GitHub still calls <paramref name="job"/>'s author a member of
    /// <paramref name="orgLogin"/>, asked on the installation token.
    ///
    /// <para><strong>An answer we could not get is a refusal.</strong> GitHub
    /// being unreachable, the app being unconfigured, the organisation login not
    /// recorded - none of those are a yes, and the cost of guessing wrong is a
    /// stranger's code compiled on the customer's own installation. So every
    /// unhappy path here returns false, and the reason is in the log.</para>
    /// </summary>
    private async Task<bool> AuthorIsStillAMemberAsync(
        GitHubPullRequestJob job, string orgLogin, AsyncServiceScope scope, CancellationToken ct)
    {
        if (orgLogin.Length == 0 || job.AuthorLogin.Length == 0)
        {
            _logger.LogInformation(
                "Dropped a fork pull-request delivery for {Job}: there is no organisation login or author to check membership for.",
                Describe(job));
            return false;
        }

        try
        {
            var github = scope.ServiceProvider.GetRequiredService<GitHubAppClient>();
            var token = await github.GetInstallationTokenAsync(job.InstallationId, ct).ConfigureAwait(false);
            if (await github.InstallationSeesOrgMemberAsync(token, orgLogin, job.AuthorLogin, ct).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Building {Job} from the author's own fork: GitHub confirms {Author} is a member of {Org}.",
                    Describe(job), job.AuthorLogin, orgLogin);
                return true;
            }

            _logger.LogInformation(
                "Dropped a fork pull-request delivery for {Job}: GitHub does not report {Author} as a member of {Org}.",
                Describe(job), job.AuthorLogin, orgLogin);
            return false;
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Dropped a fork pull-request delivery for {Job}: could not ask GitHub whether {Author} is a member of {Org}.",
                Describe(job), job.AuthorLogin, orgLogin);
            return false;
        }
    }

    /// <summary>
    /// The organisation that connected <paramref name="installationId"/>, or null
    /// when none did. Internal so a test can drive the routing without the hosted
    /// loop.
    ///
    /// <para>Each organisation is asked under its own ambient scope and its own DI
    /// scope, so <see cref="GitHubConnectionService"/> reads that organisation's
    /// settings through the ordinary tenant filter - there is no
    /// <c>IgnoreQueryFilters()</c> anywhere in this path.</para>
    /// </summary>
    internal async Task<(AmbientOrganizationScope.OrganizationIdentity Identity, string OrgLogin)?>
        ResolveOrganizationAsync(long installationId, CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one cross-org read the schedulers already make: which
            // organisations exist. organizations carries no tenant filter, so this
            // needs no bypass.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        foreach (var (orgId, isSystem) in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem);
                using var ambient = AmbientOrganizationScope.Enter(identity);
                await using var scope = _services.CreateAsyncScope();
                var connection = scope.ServiceProvider.GetRequiredService<GitHubConnectionService>();
                var status = await connection.GetStatusAsync(ct).ConfigureAwait(false);
                if (status.InstallationId == installationId)
                {
                    return (identity, status.OrgLogin ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read organisation {OrgId}'s GitHub connection while routing a webhook delivery.", orgId);
            }
        }
        return null;
    }

    /// <summary>
    /// A repository URL reduced to what identifies it: lower-cased host and path,
    /// no scheme, no credentials, no <c>.git</c> suffix, no trailing slash. Two
    /// spellings of the same repository - what an admin typed into a solution and
    /// what GitHub sent us - have to compare equal, or a tracked repository looks
    /// untracked.
    /// </summary>
    internal static string NormaliseRepositoryUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            trimmed = uri.Host + uri.AbsolutePath;
        }
        else
        {
            // A scp-style remote (git@github.com:cronus/app.git) is not a URI.
            var at = trimmed.IndexOf('@');
            if (at >= 0) trimmed = trimmed[(at + 1)..];
            trimmed = trimmed.Replace(':', '/');
        }

        trimmed = trimmed.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed.ToLowerInvariant();
    }
}
