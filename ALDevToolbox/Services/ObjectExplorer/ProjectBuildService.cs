using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using Microsoft.EntityFrameworkCore;
using OeProjectBuildResult = ALDevToolbox.Domain.Entities.ObjectExplorer.ProjectBuildResult;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The project-build pipeline core. For one project it clones each repository,
/// discovers every extension's <c>app.json</c>, resolves and downloads the
/// matching Microsoft symbols (auto-importing the parent BC release inline when
/// the catalogue lacks it), compiles each extension with <c>alc</c> in dependency
/// order (source embedded), and returns the compiled <c>.app</c>s as
/// <see cref="AppFileUpload"/>s ready for the existing ingest seam plus a per-app
/// <see cref="ProjectBuildResult"/> report. Partial failures are isolated — one
/// repo or extension that can't be cloned/compiled fails only itself. See
/// <c>.design/object-explorer-project-builds.md</c>.
///
/// <para>
/// Run by <see cref="ReleaseImportWorker"/> inside the submitter's org scope. The
/// IO (git clone, artifact download, <c>alc</c>) sits behind
/// <see cref="IProcessRunner"/> / <see cref="BcArtifactService"/> /
/// <see cref="AlCompilerProvisioner"/> so the orchestration and the pure helpers
/// (manifest parse, app discovery, dependency ordering) are unit-testable. Every
/// transient artefact lives under one temp build root deleted in
/// <c>finally</c>; the compiled <c>.app</c> bytes are buffered into memory so the
/// returned uploads outlive the root.
/// </para>
/// </summary>
public sealed class ProjectBuildService
{
    /// <summary>Temp-dir prefix for a build root, mirroring the <c>oe-artifact-</c> / <c>oe-dvd-</c> convention.</summary>
    public const string TempPrefix = "oe-build-";

    /// <summary>
    /// Hard ceiling for a discovery clone — a stalled remote becomes a logged
    /// failure, not a hang. Discovery fetches only trees + app.json blobs (no
    /// working-tree checkout), so this is ample even for repos whose history is
    /// bloated by committed binaries.
    /// </summary>
    private static readonly TimeSpan DiscoveryCloneTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Default build-clone ceiling; generous because a build clones the full working tree, which can be gigabytes when <c>.alpackages</c> binaries are committed. Override via <c>OE_BUILD_CLONE_TIMEOUT_MINUTES</c>.</summary>
    private const int DefaultBuildCloneTimeoutMinutes = 30;

    /// <summary>The build-clone ceiling (env-overridable). git's low-speed abort (~60s) catches genuine stalls; this only backstops a stuck process.</summary>
    private static TimeSpan BuildCloneTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("OE_BUILD_CLONE_TIMEOUT_MINUTES");
        return int.TryParse(raw, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : TimeSpan.FromMinutes(DefaultBuildCloneTimeoutMinutes);
    }

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly BcArtifactService _artifacts;
    private readonly ReleaseImportService _importer;
    private readonly AlCompilerProvisioner _compiler;
    private readonly OrganizationConfigService _orgConfig;
    private readonly UserRepositoryTokenService _repoTokens;
    private readonly ProjectAccess _access;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectBuildService> _logger;

    public ProjectBuildService(
        AppDbContext db,
        IOrganizationContext orgContext,
        BcArtifactService artifacts,
        ReleaseImportService importer,
        AlCompilerProvisioner compiler,
        OrganizationConfigService orgConfig,
        UserRepositoryTokenService repoTokens,
        ProjectAccess access,
        IProcessRunner processRunner,
        TimeProvider clock,
        ILogger<ProjectBuildService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _artifacts = artifacts;
        _importer = importer;
        _compiler = compiler;
        _orgConfig = orgConfig;
        _repoTokens = repoTokens;
        _access = access;
        _processRunner = processRunner;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ProjectBuildService called outside an authenticated request.");

    /// <summary>
    /// Builds <paramref name="projectId"/> into the already-created ingesting
    /// Release <paramref name="releaseId"/>: clone → discover → resolve symbols →
    /// compile. Finalises the Release's label and parent pointer, then returns the
    /// compiled uploads and the per-app report. Throws only on whole-build failures
    /// (project gone, compiler unavailable, no apps found, symbols unresolvable) —
    /// per-app problems come back as <c>failed</c> results, not exceptions.
    /// </summary>
    public async Task<ProjectBuildOutcome> BuildAsync(int projectId, int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} not found for build.");

        // The first-class build row the importer created and linked to this
        // release. The clone/changelog/log/artifact provenance hangs off it. Null
        // only for a release without a ProjectBuild (legacy / synthetic) — the new
        // persistence then no-ops, leaving the old per-app report as the record.
        var build = await _db.OeProjectBuilds
            .FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is not null)
        {
            build.Status = ProjectBuildStatus.Building;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var compiler = await _compiler.ResolveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The AL compiler isn't available yet. It's downloaded from NuGet on first use — check the server has outbound access, then retry.");

        var buildRoot = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildRoot);
        var results = new List<BuildAppResult>();
        var logs = new List<PendingLog>();
        try
        {
            // 1. Clone every repo. A clone failure fails only that repo.
            var clones = await CloneRepositoriesAsync(project, buildRoot, results, logs, ct).ConfigureAwait(false);

            // Record the per-repo commit set + changelog while the clones are still
            // on disk (the changelog runs `git log` against them). Best-effort: a
            // provenance failure never sinks the build.
            if (build is not null)
            {
                await PersistRepoProvenanceAsync(build, project, clones, logs, ct).ConfigureAwait(false);
            }

            // 2. Discover extensions across the successful clones.
            var discovered = new List<DiscoveredApp>();
            foreach (var clone in clones)
            {
                foreach (var projectDir in DiscoverAppProjectDirs(clone.Dir))
                {
                    var manifest = TryReadManifest(projectDir);
                    if (manifest is null)
                    {
                        results.Add(new BuildAppResult(Path.GetFileName(projectDir), string.Empty,
                            ProjectBuildResultStatus.Failed, "Could not read app.json in this folder.",
                            RepoUrl: clone.Url, CommitSha: clone.CommitSha, CommitDate: clone.CommitDate));
                        continue;
                    }
                    discovered.Add(new DiscoveredApp(projectDir, manifest, clone));
                }
            }
            if (discovered.Count == 0)
            {
                throw new InvalidOperationException(
                    "No buildable extensions were found. Check the repositories contain an app.json outside test folders.");
            }

            // 2b. Narrow to the extensions the user picked in the "New build"
            //     dialog (null selection = build everything, the default). A note
            //     in the log explains why the output is smaller than the repos.
            var selectedIds = ParseSelectedAppIds(build?.RequestedAppIdsJson);
            if (selectedIds is not null)
            {
                var kept = FilterBySelection(discovered, selectedIds);
                var skipped = discovered.Count - kept.Count;
                if (kept.Count == 0)
                {
                    throw new InvalidOperationException(
                        "None of the selected extensions were found in the repositories. They may have moved or been removed since the build was requested.");
                }
                if (skipped > 0)
                {
                    logs.Add(new PendingLog(null, "Build",
                        $"Compiling {kept.Count} of {discovered.Count} discovered extension(s); {skipped} were excluded by the build's selection."));
                }
                discovered = kept;
            }

            // 3. Resolve the target BC version + country, download Microsoft symbols.
            var country = ResolveCountry(project.DefaultArtifactCountry, (await _orgConfig.GetCurrentAsync(ct).ConfigureAwait(false)).Settings.AutoImportCountry);
            var majorMinor = SelectTargetMajorMinor(discovered.Select(d => d.Manifest));
            if (majorMinor is null)
            {
                throw new InvalidOperationException(
                    "None of the extensions declare an 'application' (or 'platform') version, so the matching Business Central symbols can't be resolved.");
            }

            var resolved = await _artifacts.ResolveOnPremAsync(country, majorMinor, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No Business Central artifact matched application {majorMinor} for country '{country}'. Check the version and country.");

            var symbolsDir = Path.Combine(buildRoot, "symbols");
            Directory.CreateDirectory(symbolsDir);
            var download = await _artifacts.DownloadArtifactSetAsync(resolved.ApplicationUrl, ct).ConfigureAwait(false);
            int? parentReleaseId;
            try
            {
                ExtractArtifactSymbols(download, symbolsDir);
                CopyCommittedSymbols(clones.Select(c => c.Dir).ToList(), symbolsDir);
                // Operator-supplied symbols (the manual-symbols recovery path) are
                // written last so they win over a stale committed/artifact copy of
                // the same package — the upload is the deliberate fix.
                await CopySupplementalSymbolsAsync(projectId, symbolsDir, ct).ConfigureAwait(false);
                // 4. Auto-import the parent BC release inline (best-effort) so
                //    cross-release references into Base App resolve. Reuses the
                //    artifact we already downloaded.
                parentReleaseId = await EnsureParentReleaseAsync(resolved, download, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(download.ApplicationZipPath);
                if (download.PlatformZipPath is not null) TryDelete(download.PlatformZipPath);
            }

            // 5. Compile each extension in dependency order; a compiled sibling
            //    becomes a symbol for the apps that depend on it.
            var uploads = new List<AppFileUpload>();
            var artifacts = new List<PendingArtifact>();
            foreach (var app in TopologicalOrder(discovered))
            {
                ct.ThrowIfCancellationRequested();
                var (compiled, compileLog) = await CompileAsync(app, symbolsDir, compiler, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(compileLog))
                {
                    logs.Add(new PendingLog(app.Repo.RepositoryId, $"Compile: {app.Manifest.Name}", compileLog));
                }
                if (compiled is null)
                {
                    results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                        ProjectBuildResultStatus.Failed, $"Compilation failed (see the build report for {app.Manifest.Name}).",
                        RepoUrl: app.Repo.Url, CommitSha: app.Repo.CommitSha, CommitDate: app.Repo.CommitDate));
                    continue;
                }
                // Read the .app into memory so the upload survives the temp-root
                // cleanup, and copy it into the symbol dir for dependents.
                var bytes = await File.ReadAllBytesAsync(compiled, ct).ConfigureAwait(false);
                var fileName = Path.GetFileName(compiled);
                uploads.Add(new AppFileUpload(
                    FileName: fileName,
                    AppStream: new MemoryStream(bytes, writable: false),
                    SourceZipStream: null));
                // Retain the compiled .app as a downloadable deliverable. Packaging
                // artifacts (.dep.app) are never compiler output here, but guard
                // anyway so they can't slip in as a download. See .design/artifacts.md.
                if (!fileName.EndsWith(".dep.app", StringComparison.OrdinalIgnoreCase))
                {
                    artifacts.Add(new PendingArtifact(fileName, app.Manifest.Name, app.Manifest.Version, app.Manifest.Runtime, bytes));
                }
                results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                    ProjectBuildResultStatus.Compiled, null,
                    RepoUrl: app.Repo.Url, CommitSha: app.Repo.CommitSha, CommitDate: app.Repo.CommitDate));
            }

            // Persist the retained deliverables against the build (best-effort; the
            // captured logs are persisted in the finally so they survive a throw too).
            if (build is not null)
            {
                await PersistArtifactsAsync(build, artifacts, ct).ConfigureAwait(false);
            }

            // Project labels aren't unique (the release id is their identity), so
            // a rebuild of the same project+version reuses the same clean label.
            var finalLabel = $"{project.Name} on BC {resolved.MajorMinor}";
            await FinalizeReleaseAsync(releaseId, finalLabel, parentReleaseId, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Project build for {Project} (release {ReleaseId}): {Compiled} compiled, {Failed} failed, parent release {ParentReleaseId}.",
                project.Name, releaseId, uploads.Count, results.Count(r => r.Status == ProjectBuildResultStatus.Failed), parentReleaseId);

            return new ProjectBuildOutcome(uploads, results, parentReleaseId, finalLabel, resolved.MajorMinor);
        }
        finally
        {
            // Persist whatever logs we captured even on a whole-build failure (e.g.
            // unresolved symbols throws before compile), so the user can diagnose.
            if (build is not null && logs.Count > 0)
            {
                try { await PersistLogsAsync(build, logs, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist build logs for release {ReleaseId}.", releaseId); }
            }
            TryDeleteDirectory(buildRoot);
        }
    }

    // ── Live discovery (the "New build" picker) ─────────────────────────────

    /// <summary>
    /// Clones <paramref name="projectId"/>'s repositories and returns the extensions
    /// found in them, so the "New build" dialog can let the user pick which to
    /// compile. Runs <em>in the request</em> (not the build worker) for a responsive
    /// picker; uses a shallow clone because discovery needs only the working tree,
    /// not the history the real build's changelog walks. Enforces the same gates as a
    /// build — owner/Admin (<see cref="ProjectAccess"/>) and a per-provider token —
    /// and throws <see cref="PlanValidationException"/> (keyed <c>Discovery</c>) when
    /// no extensions can be discovered, so the dialog shows the reason inline. See
    /// <c>.design/artifacts.md</c>.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredExtension>> DiscoverExtensionsAsync(int projectId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var project = await _db.OeProjects.AsNoTracking()
            .Where(c => c.Id == projectId && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Discovery"] = "This project no longer exists.",
            });

        await _access.EnsureCanManageAsync(project.CreatedByUserId, ct).ConfigureAwait(false);

        if (project.Repositories.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Discovery"] = "Add at least one repository to this project before building.",
            });
        }

        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var root = Path.Combine(Path.GetTempPath(), TempPrefix + "discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _logger.LogInformation("Discovery: cloning {RepoCount} repo(s) for project {ProjectId}.",
            project.Repositories.Count, projectId);
        var discovered = new List<DiscoveredExtension>();
        var failures = new List<string>();
        try
        {
            var index = 0;
            foreach (var repo in project.Repositories)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(root, $"repo-{index++}");
                var pat = await _repoTokens.ResolveTokenAsync(repo.Provider, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(pat))
                {
                    failures.Add($"No {repo.Provider.DisplayName()} token for \"{repo.DisplayName}\" — add one under Account → Repository tokens.");
                    continue;
                }

                var env = GitAuthEnv(repo.Provider, pat);

                // Discovery only needs app.json — never the (often gigabytes of
                // committed .alpackages) working tree. A blobless, no-checkout,
                // shallow clone fetches just commit + trees; a non-cone
                // sparse-checkout limited to app.json then materialises only those
                // files, lazily fetching only their tiny blobs. This keeps discovery
                // fast even on repos whose .git is bloated by committed binaries. The
                // PAT travels in git config (http.extraHeader), never the URL or argv.
                var clone = await _processRunner.RunAsync(new ProcessRunRequest(gitPath,
                    new[] { "clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--single-branch", "--no-tags", "--quiet", repo.Url, dest },
                    root, env, DiscoveryCloneTimeout), ct).ConfigureAwait(false);
                if (!clone.Succeeded || !Directory.Exists(dest))
                {
                    failures.Add($"Couldn't clone \"{repo.DisplayName}\": {Sanitize(clone.StdErr, pat)}".Trim());
                    _logger.LogWarning("Discovery: clone of {Repo} for project {ProjectId} failed (exit {Exit}).",
                        repo.DisplayName, projectId, clone.ExitCode);
                    continue;
                }

                // Limit the working tree to app.json (gitignore-style match at any
                // depth) and materialise it — fetches only those blobs.
                await _processRunner.RunAsync(new ProcessRunRequest(gitPath,
                    new[] { "-C", dest, "sparse-checkout", "set", "--no-cone", "app.json" }, dest, env, DiscoveryCloneTimeout), ct).ConfigureAwait(false);
                var checkout = await _processRunner.RunAsync(new ProcessRunRequest(gitPath,
                    new[] { "-C", dest, "checkout" }, dest, env, DiscoveryCloneTimeout), ct).ConfigureAwait(false);
                if (!checkout.Succeeded)
                {
                    failures.Add($"Couldn't read \"{repo.DisplayName}\": {Sanitize(checkout.StdErr, pat)}".Trim());
                    _logger.LogWarning("Discovery: checkout of {Repo} for project {ProjectId} failed (exit {Exit}).",
                        repo.DisplayName, projectId, checkout.ExitCode);
                    continue;
                }

                foreach (var projectDir in DiscoverAppProjectDirs(dest))
                {
                    var manifest = TryReadManifest(projectDir);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                    discovered.Add(new DiscoveredExtension(
                        manifest.Id, manifest.Name, manifest.Publisher, manifest.Version, repo.Url, repo.DisplayName));
                }
            }

            if (discovered.Count == 0)
            {
                var reason = failures.Count > 0
                    ? string.Join(" ", failures)
                    : "No extensions with an app.json were found outside test folders.";
                _logger.LogWarning("Discovery: found no extensions for project {ProjectId}. {Reason}", projectId, reason);
                throw new PlanValidationException(new Dictionary<string, string> { ["Discovery"] = reason });
            }

            // Stable, de-duplicated by app id (the same app cloned twice is one row),
            // ordered by name for a predictable checklist.
            var deduped = discovered
                .GroupBy(d => NormalizeAppId(d.AppId))
                .Select(g => g.First())
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("Discovery: found {Count} extension(s) for project {ProjectId}.", deduped.Count, projectId);
            return deduped;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ── Persistence helpers (worker calls these around ProcessReleaseAsync) ──

    /// <summary>
    /// Replaces the per-app build report for a release with <paramref name="results"/>.
    /// Clears any prior rows first so a rebuild/retry reports the latest attempt
    /// rather than accumulating duplicates.
    /// </summary>
    public async Task PersistResultsAsync(int releaseId, IReadOnlyList<BuildAppResult> results, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var now = _clock.GetUtcNow().UtcDateTime;

        var stale = await _db.OeProjectBuildResults
            .Where(r => r.ReleaseId == releaseId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildResults.RemoveRange(stale);

        foreach (var r in results)
        {
            _db.OeProjectBuildResults.Add(new OeProjectBuildResult
            {
                OrganizationId = orgId,
                ReleaseId = releaseId,
                AppName = Truncate(r.AppName, 250),
                AppId = Truncate(r.AppId, 50),
                Status = r.Status,
                Message = r.Message,
                RepoUrl = r.RepoUrl,
                CommitSha = r.CommitSha,
                CommitDate = r.CommitDate,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes the <c>compiled</c> rows to <c>ingested</c> once the shared
    /// importer has accepted the uploads — called by the worker after a successful
    /// <see cref="ReleaseImportService.ProcessReleaseAsync"/>.
    /// </summary>
    public async Task MarkCompiledResultsIngestedAsync(int releaseId, CancellationToken ct = default)
    {
        var rows = await _db.OeProjectBuildResults
            .Where(r => r.ReleaseId == releaseId && r.Status == ProjectBuildResultStatus.Compiled)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows) row.Status = ProjectBuildResultStatus.Ingested;
        if (rows.Count > 0) await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── ProjectBuild lifecycle (worker calls these around the build) ────────

    /// <summary>
    /// Flips the build that produced <paramref name="releaseId"/> to <c>ready</c>,
    /// stamping its BC version and finish time. No-op when the release has no
    /// <see cref="ProjectBuild"/> (legacy / synthetic). Mirrors the Release flip.
    /// </summary>
    public async Task MarkBuildReadyAsync(int releaseId, string? bcVersion, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is null) return;
        build.Status = ProjectBuildStatus.Ready;
        build.BcVersion = bcVersion ?? build.BcVersion;
        build.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flips the build that produced <paramref name="releaseId"/> to <c>failed</c>
    /// with <paramref name="message"/> and a finish time. No-op when the release
    /// has no <see cref="ProjectBuild"/>.
    /// </summary>
    public async Task MarkBuildFailedAsync(int releaseId, string message, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.FirstOrDefaultAsync(b => b.ReleaseId == releaseId, ct).ConfigureAwait(false);
        if (build is null) return;
        build.Status = ProjectBuildStatus.Failed;
        build.FailureMessage = Truncate(message, 2000);
        build.FinishedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── ProjectBuild provenance (repo commit set + changelog) ───────────────

    /// <summary>The newest N commits we record in the changelog before collapsing the tail into a summary note.</summary>
    internal const int ChangelogCommitCap = 100;

    /// <summary>
    /// Records the per-repo commit set (<see cref="ProjectBuildRepoCommit"/>) and
    /// the changelog (<see cref="ProjectBuildCommit"/>) for the build, computing the
    /// latter as <c>git log &lt;prev&gt;..&lt;HEAD&gt;</c> against the project's last
    /// <em>successful</em> build per repo. Best-effort: a provenance failure logs and
    /// returns rather than sinking the build.
    /// </summary>
    private async Task PersistRepoProvenanceAsync(ProjectBuild build, Project project, List<ClonedRepo> clones, List<PendingLog> logs, CancellationToken ct)
    {
        try
        {
            var orgId = build.OrganizationId;
            // The commit set for this build.
            foreach (var clone in clones)
            {
                _db.OeProjectBuildRepoCommits.Add(new ProjectBuildRepoCommit
                {
                    OrganizationId = orgId,
                    ProjectBuildId = build.Id,
                    ProjectRepositoryId = clone.RepositoryId,
                    RepoUrl = Truncate(clone.Url, 2000),
                    RepoDisplayName = Truncate(clone.DisplayName, 250),
                    CommitHash = Truncate(clone.CommitSha ?? string.Empty, 64),
                    CommittedAt = clone.CommitDate,
                });
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await ComputeAndPersistChangelogAsync(build, project, clones, logs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record build provenance for build {BuildId} (release {ReleaseId}).", build.Id, build.ReleaseId);
        }
    }

    /// <summary>
    /// Computes and persists the per-repo changelog for the build. For each repo it
    /// finds the commit the project's last successful build pinned, then records
    /// <c>git log &lt;prev&gt;..&lt;HEAD&gt;</c>. A repo with no prior build, or whose
    /// previous commit is no longer an ancestor (force-push / rebase), gets a single
    /// summary note instead of a commit list. Over-cap ranges are truncated with a
    /// "...and N more" note.
    /// </summary>
    private async Task ComputeAndPersistChangelogAsync(ProjectBuild build, Project project, List<ClonedRepo> clones, List<PendingLog> logs, CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var orgId = build.OrganizationId;

        // The previous successful build's commit per repo (the changelog baseline).
        var prevBuildId = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == project.Id && b.Id != build.Id && b.Status == ProjectBuildStatus.Ready)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var prevByRepo = new Dictionary<int, string>();
        if (prevBuildId is not null)
        {
            var prevCommits = await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => c.ProjectBuildId == prevBuildId && c.ProjectRepositoryId != null && c.CommitHash != "")
                .Select(c => new { RepoId = c.ProjectRepositoryId!.Value, c.CommitHash })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var c in prevCommits) prevByRepo[c.RepoId] = c.CommitHash;
        }

        foreach (var clone in clones)
        {
            if (clone.RepositoryId is null || clone.CommitSha is null) continue;

            var rows = new List<ProjectBuildCommit>();

            if (!prevByRepo.TryGetValue(clone.RepositoryId.Value, out var prevSha))
            {
                rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                    "First build of this repository — no previous successful build to compare against."));
            }
            else if (prevSha == clone.CommitSha)
            {
                rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value, "No new commits since the last successful build."));
            }
            else
            {
                var ancestry = await _processRunner.RunAsync(new ProcessRunRequest(
                    gitPath, new[] { "-C", clone.Dir, "merge-base", "--is-ancestor", prevSha, "HEAD" }, clone.Dir), ct).ConfigureAwait(false);
                if (!ancestry.Succeeded)
                {
                    rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                        $"The previous build's commit ({Short(prevSha)}) is no longer in history — the branch was force-pushed or rebased, so the changelog can't be computed."));
                }
                else
                {
                    var log = await _processRunner.RunAsync(new ProcessRunRequest(
                        gitPath,
                        new[] { "-C", clone.Dir, "log", "--no-merges", "-n", (ChangelogCommitCap + 1).ToString(),
                                "--pretty=format:%h%an%cI%s", $"{prevSha}..HEAD" },
                        clone.Dir), ct).ConfigureAwait(false);
                    var (parsed, truncated) = ParseChangelog(log.StdOut, ChangelogCommitCap);
                    var ordering = 0;
                    foreach (var entry in parsed)
                    {
                        rows.Add(new ProjectBuildCommit
                        {
                            OrganizationId = orgId,
                            ProjectBuildId = build.Id,
                            ProjectRepositoryId = clone.RepositoryId,
                            ShortHash = Truncate(entry.ShortHash, 64),
                            Message = entry.Subject,
                            Author = Truncate(entry.Author, 250),
                            CommittedAt = entry.CommittedAt,
                            Ordering = ordering++,
                        });
                    }
                    if (truncated)
                    {
                        var more = SummaryNote(orgId, build.Id, clone.RepositoryId.Value,
                            $"...and more commits not shown (the changelog is capped at {ChangelogCommitCap}).");
                        more.Ordering = ordering;
                        rows.Add(more);
                    }
                    if (parsed.Count == 0 && !truncated)
                    {
                        rows.Add(SummaryNote(orgId, build.Id, clone.RepositoryId.Value, "No new commits since the last successful build."));
                    }
                }
            }

            _db.OeProjectBuildCommits.AddRange(rows);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ProjectBuildCommit SummaryNote(int orgId, int buildId, int? repoId, string text) => new()
    {
        OrganizationId = orgId,
        ProjectBuildId = buildId,
        ProjectRepositoryId = repoId,
        ShortHash = string.Empty,
        Message = text,
        Author = string.Empty,
        CommittedAt = null,
        Ordering = 0,
    };

    /// <summary>
    /// Parses <c>git log</c> output formatted as short-hash / author / committer-date
    /// / subject, separated by the ASCII unit-separator (0x1F) with one commit per
    /// line, into changelog entries. Returns whether the range exceeded
    /// <paramref name="cap"/> (the caller passed <c>cap + 1</c> to <c>-n</c>).
    /// </summary>
    internal static (IReadOnlyList<ChangelogEntry> Entries, bool Truncated) ParseChangelog(string stdout, int cap)
    {
        var entries = new List<ChangelogEntry>();
        if (string.IsNullOrWhiteSpace(stdout)) return (entries, false);

        foreach (var line in stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            var parts = line.Split('');
            if (parts.Length < 4) continue;
            DateTime? date = DateTimeOffset.TryParse(parts[2].Trim(), out var dto) ? dto.UtcDateTime : null;
            entries.Add(new ChangelogEntry(parts[0].Trim(), parts[1].Trim(), date, parts[3].Trim()));
        }

        var truncated = entries.Count > cap;
        if (truncated) entries = entries.Take(cap).ToList();
        return (entries, truncated);
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    // ── ProjectBuild artifacts + logs ───────────────────────────────────────

    /// <summary>Replaces the build's retained deliverables with <paramref name="artifacts"/> (clears stale rows so a retry doesn't duplicate).</summary>
    private async Task PersistArtifactsAsync(ProjectBuild build, List<PendingArtifact> artifacts, CancellationToken ct)
    {
        var stale = await _db.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == build.Id).ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildArtifacts.RemoveRange(stale);

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var a in artifacts)
        {
            _db.OeProjectBuildArtifacts.Add(new ProjectBuildArtifact
            {
                OrganizationId = build.OrganizationId,
                ProjectBuildId = build.Id,
                FileName = Truncate(a.FileName, 400),
                AppName = Truncate(a.AppName, 250),
                AppVersion = Truncate(a.AppVersion, 50),
                RuntimeVersion = a.Runtime is null ? null : Truncate(a.Runtime, 50),
                SizeBytes = a.Content.LongLength,
                Content = a.Content,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the build's logs with <paramref name="logs"/> (idempotent across the success-path and the failure finally).</summary>
    private async Task PersistLogsAsync(ProjectBuild build, List<PendingLog> logs, CancellationToken ct)
    {
        var stale = await _db.OeProjectBuildLogs.Where(l => l.ProjectBuildId == build.Id).ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeProjectBuildLogs.RemoveRange(stale);

        var now = _clock.GetUtcNow().UtcDateTime;
        var ordering = 0;
        foreach (var l in logs)
        {
            _db.OeProjectBuildLogs.Add(new ProjectBuildLog
            {
                OrganizationId = build.OrganizationId,
                ProjectBuildId = build.Id,
                ProjectRepositoryId = l.RepoId,
                Section = Truncate(l.Section, 250),
                Content = l.Content,
                Ordering = ordering++,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Clone ───────────────────────────────────────────────────────────

    private async Task<List<ClonedRepo>> CloneRepositoriesAsync(
        Project project, string buildRoot, List<BuildAppResult> results, List<PendingLog> logs, CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var clones = new List<ClonedRepo>();
        var index = 0;
        foreach (var repo in project.Repositories)
        {
            var dest = Path.Combine(buildRoot, $"repo-{index++}");
            var pat = await _repoTokens.ResolveTokenAsync(repo.Provider, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pat))
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                    $"You don't have a {repo.Provider.DisplayName()} token set. Add one under Account → Repository tokens, then rebuild.",
                    RepoUrl: repo.Url));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName,
                    $"Skipped: no {repo.Provider.DisplayName()} token for the user who started this build."));
                continue;
            }

            // A blobless single-branch clone keeps the full commit history (so the
            // changelog's `git log <prev>..<new>` and the force-push ancestry check
            // work) while fetching file blobs lazily on checkout — close to a
            // depth-1 clone's transfer for the working tree, but with the metadata
            // the changelog needs. The PAT travels in the environment
            // (GIT_CONFIG_* http.extraHeader), never in the URL, on disk, or in the
            // world-readable process argv.
            var args = new List<string> { "clone", "--filter=blob:none", "--single-branch", "--quiet", repo.Url, dest };
            var env = GitAuthEnv(repo.Provider, pat);
            var result = await _processRunner.RunAsync(new ProcessRunRequest(gitPath, args, buildRoot, env, BuildCloneTimeout()), ct).ConfigureAwait(false);
            var cloneLog = Sanitize(string.Join("\n", new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim(), pat);
            if (result.Succeeded && Directory.Exists(dest))
            {
                var (sha, date) = await CaptureCommitAsync(gitPath, dest, ct).ConfigureAwait(false);
                clones.Add(new ClonedRepo(dest, repo.Url, sha, date, repo.Id, repo.DisplayName));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName,
                    $"Cloned {repo.Url} at {(sha is null ? "(unknown commit)" : sha)}.{(cloneLog.Length > 0 ? "\n" + cloneLog : "")}"));
            }
            else
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, ProjectBuildResultStatus.Failed,
                    $"git clone failed: {Sanitize(result.StdErr, pat)}".Trim(), RepoUrl: repo.Url));
                logs.Add(new PendingLog(repo.Id, repo.DisplayName, $"git clone failed (exit {result.ExitCode}): {cloneLog}".Trim()));
                _logger.LogWarning("Project {ProjectId}: clone of {Repo} exited {Exit}.", project.Id, repo.DisplayName, result.ExitCode);
            }
        }
        return clones;
    }

    /// <summary>
    /// Reads the cloned repo's pinned commit — full SHA + committer date (UTC) —
    /// for build provenance. Best-effort: a failure (shallow clone oddity, missing
    /// HEAD) returns nulls rather than failing the build.
    /// </summary>
    private async Task<(string? Sha, DateTime? Date)> CaptureCommitAsync(string gitPath, string cloneDir, CancellationToken ct)
    {
        try
        {
            // %H = full SHA, %cI = committer date (strict ISO-8601), tab-separated.
            var r = await _processRunner.RunAsync(new ProcessRunRequest(
                gitPath, new[] { "-C", cloneDir, "show", "-s", "--format=%H%x09%cI", "HEAD" }, cloneDir), ct).ConfigureAwait(false);
            if (!r.Succeeded) return (null, null);
            var parts = r.StdOut.Trim().Split('\t');
            var sha = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : null;
            DateTime? date = parts.Length > 1 && DateTimeOffset.TryParse(parts[1].Trim(), out var dto)
                ? dto.UtcDateTime : null;
            return (sha, date);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the commit for {CloneDir}; build provenance will be incomplete.", cloneDir);
            return (null, null);
        }
    }

    // ── Symbols ─────────────────────────────────────────────────────────

    /// <summary>Extracts every Microsoft <c>.app</c> from the downloaded artifact set into <paramref name="symbolsDir"/>.</summary>
    private void ExtractArtifactSymbols(BcArtifactDownload download, string symbolsDir)
    {
        var openedStreams = new List<Stream>();
        try
        {
            var (appUploads, appArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.ApplicationZipPath, isPlatform: false, openedStreams);
            using (appArchive) WriteSymbolApps(appUploads, symbolsDir);
            if (download.PlatformZipPath is not null)
            {
                var (platUploads, platArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.PlatformZipPath, isPlatform: true, openedStreams);
                using (platArchive) WriteSymbolApps(platUploads, symbolsDir);
            }
        }
        finally
        {
            foreach (var s in openedStreams) { try { s.Dispose(); } catch { /* swallow */ } }
        }
    }

    private static void WriteSymbolApps(IReadOnlyList<AppFileUpload> uploads, string symbolsDir)
    {
        foreach (var upload in uploads)
        {
            var dest = Path.Combine(symbolsDir, Path.GetFileName(upload.FileName));
            using var file = File.Create(dest);
            upload.AppStream.CopyTo(file);
        }
    }

    /// <summary>
    /// Writes any operator-supplied dependency symbols (the manual-symbols recovery
    /// path) for the project into the symbol dir, overwriting a same-named copy so
    /// the upload — the deliberate fix for a dependency missing from both the repo's
    /// <c>.alpackages/</c> and any Microsoft artifact — takes effect. Persisted at
    /// the project level, so every later build benefits. See
    /// <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    private async Task CopySupplementalSymbolsAsync(int projectId, string symbolsDir, CancellationToken ct)
    {
        var symbols = await _db.OeProjectSymbols.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => new { s.FileName, s.Content })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var symbol in symbols)
        {
            var dest = Path.Combine(symbolsDir, Path.GetFileName(symbol.FileName));
            await File.WriteAllBytesAsync(dest, symbol.Content, ct).ConfigureAwait(false);
        }
        if (symbols.Count > 0)
        {
            _logger.LogInformation("Merged {Count} supplemental symbol(s) into the build cache for project {ProjectId}.",
                symbols.Count, projectId);
        }
    }

    /// <summary>Copies any third-party symbols the repos committed under <c>.alpackages/</c> into the symbol dir.</summary>
    private static void CopyCommittedSymbols(IReadOnlyList<string> cloneDirs, string symbolsDir)
    {
        foreach (var cloneDir in cloneDirs)
        {
            foreach (var pkgDir in Directory.EnumerateDirectories(cloneDir, ".alpackages", SearchOption.AllDirectories))
            {
                foreach (var app in Directory.EnumerateFiles(pkgDir, "*.app", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(symbolsDir, Path.GetFileName(app));
                    if (!File.Exists(dest)) File.Copy(app, dest);
                }
            }
        }
    }

    /// <summary>
    /// Ensures a non-deleted first-party Release exists for the resolved artifact
    /// (so the project Release's <c>ParentReleaseId</c> can point at it), importing
    /// it inline from the already-downloaded zips when absent. Best-effort: a failed
    /// parent import logs and returns null rather than sinking the project build.
    /// </summary>
    private async Task<int?> EnsureParentReleaseAsync(ResolvedArtifact resolved, BcArtifactDownload download, CancellationToken ct)
    {
        var existing = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DedupKey == resolved.DedupKey && r.DeletedAt == null)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        try
        {
            var metadata = new ReleaseImportMetadata(
                Label: resolved.Label, Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null,
                DedupKey: resolved.DedupKey);
            var parentId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

            var openedStreams = new List<Stream>();
            System.IO.Compression.ZipArchive? appArchive = null;
            System.IO.Compression.ZipArchive? platArchive = null;
            try
            {
                List<AppFileUpload> uploads;
                (uploads, appArchive) = ReleaseZipStaging.OpenBcArtifactZip(download.ApplicationZipPath, isPlatform: false, openedStreams);
                if (download.PlatformZipPath is not null)
                {
                    var (platUploads, archive) = ReleaseZipStaging.OpenBcArtifactZip(download.PlatformZipPath, isPlatform: true, openedStreams);
                    platArchive = archive;
                    uploads = uploads.Concat(platUploads).ToList();
                }
                await _importer.ProcessReleaseAsync(parentId, uploads, storeSymbolReference: false, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var s in openedStreams) { try { s.Dispose(); } catch { /* swallow */ } }
                appArchive?.Dispose();
                platArchive?.Dispose();
            }

            _logger.LogInformation("Auto-imported parent BC release {Label} (release {ParentId}) for a project build.", resolved.Label, parentId);
            return parentId;
        }
        catch (Exception ex)
        {
            // A concurrent first-party/artifact import (independent of the
            // single-reader project-build worker) may have won the unique-
            // dedup_key insert, surfacing here as a Postgres 23505. Re-query: if a
            // good parent release now exists, adopt it rather than losing the
            // cross-release link by returning null. See issue #431.
            _db.ChangeTracker.Clear();
            var adopted = await _db.OeReleases.AsNoTracking()
                .Where(r => r.DedupKey == resolved.DedupKey && r.DeletedAt == null)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (adopted is not null)
            {
                _logger.LogInformation(
                    "Adopted concurrently-created parent BC release {Label} (release {ParentId}) for a project build.",
                    resolved.Label, adopted);
                return adopted;
            }

            _logger.LogError(ex,
                "Failed to auto-import the parent BC release {Label}; the project build continues without cross-release resolution.",
                resolved.Label);
            return null;
        }
    }

    // ── Compile ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles one extension with <c>alc</c> against <paramref name="symbolsDir"/>,
    /// returning the output <c>.app</c> path (null on failure) and the captured
    /// compiler output for the build log. The output lands in the symbol dir so
    /// apps later in the order can depend on it.
    /// </summary>
    private async Task<(string? OutFile, string Log)> CompileAsync(DiscoveredApp app, string symbolsDir, AlCompilerInfo compiler, CancellationToken ct)
    {
        var outFile = Path.Combine(symbolsDir, SafeAppFileName(app.Manifest));
        var args = new List<string>
        {
            "/project:" + app.ProjectDir,
            "/packagecachepath:" + symbolsDir,
            "/out:" + outFile,
        };
        // A net8-targeted compiler needs roll-forward to run on the net10 host.
        var env = compiler.NeedsRollForward
            ? new Dictionary<string, string> { ["DOTNET_ROLL_FORWARD"] = "LatestMajor" }
            : null;

        var result = await _processRunner.RunAsync(new ProcessRunRequest(compiler.AlcPath, args, app.ProjectDir, env), ct).ConfigureAwait(false);
        // alc writes diagnostics to stdout; keep both streams for the build log.
        var log = string.Join("\n", new[] { result.StdOut, result.StdErr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (result.Succeeded && File.Exists(outFile))
        {
            return (outFile, log);
        }
        _logger.LogWarning("alc failed for {App} (exit {Exit}): {Err}", app.Manifest.Name, result.ExitCode,
            Truncate(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr, 2000));
        return (null, log);
    }

    // ── Release finalisation ────────────────────────────────────────────

    private async Task FinalizeReleaseAsync(int releaseId, string label, int? parentReleaseId, CancellationToken ct)
    {
        var release = await _db.OeReleases.FirstOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false);
        if (release is null) return;
        release.Label = label;
        release.ParentReleaseId = parentReleaseId;
        release.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Pure helpers (unit-tested) ──────────────────────────────────────

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".alpackages", ".vscode", ".git", ".github", "node_modules", ".snapshots", ".altestrunner",
    };

    // Mirrors FolderZipWalker's test-folder rules so the two ingest paths agree
    // on what counts as a test extension.
    private static readonly HashSet<string> TestFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test", "Tests", "Test Library", "Test Libraries", "TestLibraries", "TestFramework",
    };

    private static readonly string[] TestFolderSuffixes =
    {
        " Test Library", " Test Libraries", " Test Toolkit", " Tests",
    };

    internal static bool IsTestSegment(string segment) =>
        TestFolderNames.Contains(segment)
        || TestFolderSuffixes.Any(suf => segment.EndsWith(suf, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Walks <paramref name="root"/> for folders containing an <c>app.json</c>,
    /// pruning excluded (<c>.alpackages</c>, <c>.git</c>, …) and test folders during
    /// descent. Returns the project directories (the folders holding app.json).
    /// </summary>
    internal static IReadOnlyList<string> DiscoverAppProjectDirs(string root)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (File.Exists(Path.Combine(dir, "app.json"))) results.Add(dir);
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (ExcludedDirs.Contains(name) || IsTestSegment(name)) continue;
                stack.Push(sub);
            }
        }
        return results;
    }

    /// <summary>Parses an <c>app.json</c> body into a manifest, tolerant of trailing commas / comments. Null when unreadable.</summary>
    internal static AppJsonManifest? ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string Str(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            string? StrOrNull(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var deps = new List<AppJsonDependency>();
            if (root.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depsEl.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    // Old app.json used "appId"; new uses "id".
                    var depId = (d.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString()
                              : d.TryGetProperty("appId", out var aidv) && aidv.ValueKind == JsonValueKind.String ? aidv.GetString() : null) ?? string.Empty;
                    var depName = d.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString()! : string.Empty;
                    if (depId.Length > 0) deps.Add(new AppJsonDependency(depId, depName));
                }
            }

            var id = Str("id");
            if (id.Length == 0) id = Str("appId");
            return new AppJsonManifest(id, Str("name"), Str("publisher"), Str("version"), StrOrNull("application"), StrOrNull("platform"), StrOrNull("runtime"), deps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The highest <c>application</c> (else <c>platform</c>) Major.Minor any extension requires, or null when none declare one.</summary>
    internal static string? SelectTargetMajorMinor(IEnumerable<AppJsonManifest> manifests)
    {
        Version? best = null;
        foreach (var m in manifests)
        {
            var raw = !string.IsNullOrWhiteSpace(m.Application) ? m.Application : m.Platform;
            if (Version.TryParse(raw, out var v) && (best is null || v > best)) best = v;
        }
        return best is null ? null : $"{best.Major}.{best.Minor}";
    }

    /// <summary>
    /// Orders extensions dependencies-first: an app comes after every app in the set
    /// it (transitively) depends on, so a sibling is compiled before its dependents.
    /// Cycle-safe (best-effort) and stable in input order.
    /// </summary>
    internal static IReadOnlyList<DiscoveredApp> TopologicalOrder(IReadOnlyList<DiscoveredApp> apps)
    {
        var byId = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
        {
            if (a.Manifest.Id.Length > 0) byId[a.Manifest.Id] = a;
        }

        var ordered = new List<DiscoveredApp>();
        // 0 = unvisited, 1 = on the stack (visiting), 2 = emitted.
        var state = new Dictionary<DiscoveredApp, int>();

        void Visit(DiscoveredApp app)
        {
            if (state.TryGetValue(app, out var s) && s != 0) return; // visiting (cycle) or done
            state[app] = 1;
            foreach (var dep in app.Manifest.Dependencies)
            {
                if (byId.TryGetValue(dep.Id, out var depApp) && !ReferenceEquals(depApp, app))
                {
                    Visit(depApp);
                }
            }
            state[app] = 2;
            ordered.Add(app);
        }

        foreach (var a in apps) Visit(a);
        return ordered;
    }

    /// <summary>The git <c>http.extraHeader</c> value carrying basic auth for the provider's PAT.</summary>
    internal static string BasicAuthHeaderValue(RepositoryProvider provider, string pat)
    {
        // Azure DevOps: empty username, PAT as password. GitHub: PAT as the
        // password behind the conventional x-access-token username.
        var raw = provider == RepositoryProvider.GitHub ? $"x-access-token:{pat}" : $":{pat}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return $"Authorization: Basic {b64}";
    }

    /// <summary>
    /// Builds the environment for a git invocation that needs the PAT, carrying
    /// the basic-auth header via <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_0</c>/
    /// <c>GIT_CONFIG_VALUE_0</c> rather than a <c>-c http.extraHeader=…</c> argv.
    /// The app process is multi-tenant; an argv is visible via the world-readable
    /// <c>/proc/&lt;pid&gt;/cmdline</c>, whereas the environment block isn't.
    /// Always sets <c>GIT_TERMINAL_PROMPT=0</c> so a bad token fails fast. See #430.
    /// </summary>
    private static Dictionary<string, string> GitAuthEnv(RepositoryProvider provider, string pat) => new()
    {
        // Never block on an interactive prompt or a credential helper — the PAT
        // travels in http.extraHeader. A configured helper (manager/cache/store) on
        // the host could otherwise stall the clone indefinitely; disabling it
        // (credential.helper="") plus a low-speed abort turns a hung/unreachable
        // remote into a fast, non-zero failure instead of a forever-hang.
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_CONFIG_COUNT"] = "4",
        ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
        ["GIT_CONFIG_VALUE_0"] = BasicAuthHeaderValue(provider, pat),
        ["GIT_CONFIG_KEY_1"] = "credential.helper",
        ["GIT_CONFIG_VALUE_1"] = "",
        ["GIT_CONFIG_KEY_2"] = "http.lowSpeedLimit",
        ["GIT_CONFIG_VALUE_2"] = "1000",
        ["GIT_CONFIG_KEY_3"] = "http.lowSpeedTime",
        ["GIT_CONFIG_VALUE_3"] = "60",
    };

    /// <summary>
    /// Parses the build's stored selection (a JSON array of app-id strings) into a
    /// normalised set. Returns <c>null</c> for a null/blank/invalid value — meaning
    /// "build everything", the default and the back-compat behaviour for resumed or
    /// migration-synthesised builds.
    /// </summary>
    internal static IReadOnlySet<string>? ParseSelectedAppIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null) return null;
            return ids.Select(NormalizeAppId).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps only the discovered apps whose manifest id is in <paramref name="selectedIds"/>
    /// (compared on the normalised id). The pure core of the per-build extension
    /// selection, separated so it's unit-testable without a clone.
    /// </summary>
    internal static List<DiscoveredApp> FilterBySelection(IReadOnlyList<DiscoveredApp> discovered, IReadOnlySet<string> selectedIds) =>
        discovered.Where(d => selectedIds.Contains(NormalizeAppId(d.Manifest.Id))).ToList();

    /// <summary>
    /// Canonicalises an app-id GUID for comparison — trims, strips surrounding
    /// braces, and lower-cases — so a selection captured at discovery still matches
    /// the manifest read at build time regardless of brace/case formatting.
    /// </summary>
    internal static string NormalizeAppId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>Normalises the country fallback chain: per-project → org default → <c>w1</c>.</summary>
    internal static string ResolveCountry(string? projectCountry, string? orgCountry)
    {
        if (!string.IsNullOrWhiteSpace(projectCountry)) return projectCountry.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(orgCountry)) return orgCountry.Trim().ToLowerInvariant();
        return "w1";
    }

    private static AppJsonManifest? TryReadManifest(string projectDir)
    {
        try { return ParseManifest(File.ReadAllText(Path.Combine(projectDir, "app.json"))); }
        catch { return null; }
    }

    private static string SafeAppFileName(AppJsonManifest m)
    {
        var stem = $"{m.Publisher}_{m.Name}_{m.Version}";
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(stem.Replace("_", ""))) stem = m.Id.Length > 0 ? m.Id : Guid.NewGuid().ToString("N");
        return stem + ".app";
    }

    /// <summary>
    /// Strips any accidental occurrence of the PAT from a tool's output before
    /// it's stored/logged. Redacts the raw PAT and the base64 basic-auth forms
    /// it's actually carried as in git config (GitHub <c>x-access-token:pat</c>,
    /// Azure <c>:pat</c>), in case a verbose git error echoes the header value.
    /// Defense-in-depth: the PAT lives in git config, not the URL, so it
    /// shouldn't reach these streams in the first place (#434). The compiler and
    /// commit-capture invocations never receive the PAT, so their output can't
    /// carry it — only git transport (clone / ls-remote) is routed through here.
    /// </summary>
    private static string Sanitize(string text, string secret)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
        text = text.Replace(secret, "***");
        foreach (var form in new[] { $"x-access-token:{secret}", $":{secret}" })
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(form));
            text = text.Replace(b64, "***");
        }
        return text;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex)
        {
            // Best-effort, but observable: a failed cleanup leaves cloned
            // project source + downloaded symbols on the shared temp volume,
            // which should be visible rather than silently accumulating. #435
            _logger.LogWarning(ex,
                "Failed to clean up the project build directory {BuildRoot}; cloned source and downloaded symbols may remain on the temp volume.",
                path);
        }
    }

    /// <summary>A captured log section accumulated during a build, before it's persisted as a <see cref="ProjectBuildLog"/>.</summary>
    private sealed record PendingLog(int? RepoId, string Section, string Content);

    /// <summary>A compiled deliverable held in memory, before it's persisted as a <see cref="ProjectBuildArtifact"/>.</summary>
    private sealed record PendingArtifact(string FileName, string AppName, string AppVersion, string? Runtime, byte[] Content);
}

/// <summary>A discovered extension: the folder holding its app.json, the parsed manifest, and the repo it came from.</summary>
public sealed record DiscoveredApp(string ProjectDir, AppJsonManifest Manifest, ClonedRepo Repo);

/// <summary>
/// One extension surfaced by the live discovery clone for the "New build" picker:
/// its app-id (the stable selector persisted on the build), display fields, and the
/// repository it came from. Carries no file paths or bytes — it's a UI choice list.
/// </summary>
public sealed record DiscoveredExtension(
    string AppId,
    string Name,
    string Publisher,
    string Version,
    string RepoUrl,
    string RepoDisplayName);

/// <summary>
/// A successfully cloned repository plus the commit it's pinned at — provenance
/// carried onto each built app and persisted as a <see cref="ProjectBuildRepoCommit"/>.
/// <see cref="RepositoryId"/> / <see cref="DisplayName"/> identify the source
/// <see cref="ProjectRepository"/> so the per-repo changelog and build record link back.
/// </summary>
public sealed record ClonedRepo(string Dir, string Url, string? CommitSha, DateTime? CommitDate, int? RepositoryId = null, string DisplayName = "");

/// <summary>The fields the build pipeline reads from an <c>app.json</c>.</summary>
public sealed record AppJsonManifest(
    string Id,
    string Name,
    string Publisher,
    string Version,
    string? Application,
    string? Platform,
    string? Runtime,
    IReadOnlyList<AppJsonDependency> Dependencies);

/// <summary>One inter-app dependency declared in <c>app.json</c> (id + name).</summary>
public sealed record AppJsonDependency(string Id, string Name);

/// <summary>One extension's outcome from a build, before it's persisted as a <see cref="ProjectBuildResult"/> row. Carries the source provenance (repo + commit) when known.</summary>
public sealed record BuildAppResult(
    string AppName,
    string AppId,
    string Status,
    string? Message,
    string? RepoUrl = null,
    string? CommitSha = null,
    DateTime? CommitDate = null);

/// <summary>
/// The product of a project build: the compiled uploads ready for the shared
/// ingest seam, the per-app report, the resolved parent release (when known), the
/// finalised Release label, and the resolved BC Major.Minor the build compiled
/// against (stamped onto the <see cref="ProjectBuild"/>).
/// </summary>
public sealed record ProjectBuildOutcome(
    IReadOnlyList<AppFileUpload> Uploads,
    IReadOnlyList<BuildAppResult> Results,
    int? ParentReleaseId,
    string? FinalLabel,
    string? BcVersion = null);

/// <summary>One parsed changelog commit (short hash, author, committer date, subject).</summary>
public sealed record ChangelogEntry(string ShortHash, string Author, DateTime? CommittedAt, string Subject);
