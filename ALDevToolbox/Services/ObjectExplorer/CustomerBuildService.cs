using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using OeCustomerBuildResult = ALDevToolbox.Domain.Entities.ObjectExplorer.CustomerBuildResult;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The customer-build pipeline core. For one customer it clones each repository,
/// discovers every extension's <c>app.json</c>, resolves and downloads the
/// matching Microsoft symbols (auto-importing the parent BC release inline when
/// the catalogue lacks it), compiles each extension with <c>alc</c> in dependency
/// order (source embedded), and returns the compiled <c>.app</c>s as
/// <see cref="AppFileUpload"/>s ready for the existing ingest seam plus a per-app
/// <see cref="CustomerBuildResult"/> report. Partial failures are isolated — one
/// repo or extension that can't be cloned/compiled fails only itself. See
/// <c>.design/object-explorer-customer-builds.md</c>.
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
public sealed class CustomerBuildService
{
    /// <summary>Temp-dir prefix for a build root, mirroring the <c>oe-artifact-</c> / <c>oe-dvd-</c> convention.</summary>
    public const string TempPrefix = "oe-build-";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly BcArtifactService _artifacts;
    private readonly ReleaseImportService _importer;
    private readonly AlCompilerProvisioner _compiler;
    private readonly OrganizationConfigService _orgConfig;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _clock;
    private readonly ILogger<CustomerBuildService> _logger;

    public CustomerBuildService(
        AppDbContext db,
        IOrganizationContext orgContext,
        BcArtifactService artifacts,
        ReleaseImportService importer,
        AlCompilerProvisioner compiler,
        OrganizationConfigService orgConfig,
        IProcessRunner processRunner,
        TimeProvider clock,
        ILogger<CustomerBuildService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _artifacts = artifacts;
        _importer = importer;
        _compiler = compiler;
        _orgConfig = orgConfig;
        _processRunner = processRunner;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; CustomerBuildService called outside an authenticated request.");

    /// <summary>
    /// Builds <paramref name="customerId"/> into the already-created ingesting
    /// Release <paramref name="releaseId"/>: clone → discover → resolve symbols →
    /// compile. Finalises the Release's label and parent pointer, then returns the
    /// compiled uploads and the per-app report. Throws only on whole-build failures
    /// (customer gone, compiler unavailable, no apps found, symbols unresolvable) —
    /// per-app problems come back as <c>failed</c> results, not exceptions.
    /// </summary>
    public async Task<CustomerBuildOutcome> BuildAsync(int customerId, int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var customer = await _db.OeCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Customer {customerId} not found for build.");

        var compiler = await _compiler.ResolveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The AL compiler isn't available yet. It's downloaded from NuGet on first use — check the server has outbound access, then retry.");

        var buildRoot = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildRoot);
        var results = new List<BuildAppResult>();
        try
        {
            // 1. Clone every repo. A clone failure fails only that repo.
            var cloneDirs = await CloneRepositoriesAsync(customer, buildRoot, results, ct).ConfigureAwait(false);

            // 2. Discover extensions across the successful clones.
            var discovered = new List<DiscoveredApp>();
            foreach (var cloneDir in cloneDirs)
            {
                foreach (var projectDir in DiscoverAppProjectDirs(cloneDir))
                {
                    var manifest = TryReadManifest(projectDir);
                    if (manifest is null)
                    {
                        results.Add(new BuildAppResult(Path.GetFileName(projectDir), string.Empty,
                            CustomerBuildResultStatus.Failed, "Could not read app.json in this folder."));
                        continue;
                    }
                    discovered.Add(new DiscoveredApp(projectDir, manifest));
                }
            }
            if (discovered.Count == 0)
            {
                throw new InvalidOperationException(
                    "No buildable extensions were found. Check the repositories contain an app.json outside test folders.");
            }

            // 3. Resolve the target BC version + country, download Microsoft symbols.
            var country = ResolveCountry(customer.DefaultArtifactCountry, (await _orgConfig.GetCurrentAsync(ct).ConfigureAwait(false)).Settings.AutoImportCountry);
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
                CopyCommittedSymbols(cloneDirs, symbolsDir);
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
            foreach (var app in TopologicalOrder(discovered))
            {
                ct.ThrowIfCancellationRequested();
                var compiled = await CompileAsync(app, symbolsDir, compiler, ct).ConfigureAwait(false);
                if (compiled is null)
                {
                    results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                        CustomerBuildResultStatus.Failed, $"Compilation failed (see the build report for {app.Manifest.Name})."));
                    continue;
                }
                // Read the .app into memory so the upload survives the temp-root
                // cleanup, and copy it into the symbol dir for dependents.
                var bytes = await File.ReadAllBytesAsync(compiled, ct).ConfigureAwait(false);
                uploads.Add(new AppFileUpload(
                    FileName: Path.GetFileName(compiled),
                    AppStream: new MemoryStream(bytes, writable: false),
                    SourceZipStream: null));
                results.Add(new BuildAppResult(app.Manifest.Name, app.Manifest.Id,
                    CustomerBuildResultStatus.Compiled, null));
            }

            var finalLabel = await PickUniqueLabelAsync(
                $"{customer.Name} on BC {resolved.MajorMinor}", releaseId, ct).ConfigureAwait(false);
            await FinalizeReleaseAsync(releaseId, finalLabel, parentReleaseId, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Customer build for {Customer} (release {ReleaseId}): {Compiled} compiled, {Failed} failed, parent release {ParentReleaseId}.",
                customer.Name, releaseId, uploads.Count, results.Count(r => r.Status == CustomerBuildResultStatus.Failed), parentReleaseId);

            return new CustomerBuildOutcome(uploads, results, parentReleaseId, finalLabel);
        }
        finally
        {
            TryDeleteDirectory(buildRoot);
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

        var stale = await _db.OeCustomerBuildResults
            .Where(r => r.ReleaseId == releaseId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count > 0) _db.OeCustomerBuildResults.RemoveRange(stale);

        foreach (var r in results)
        {
            _db.OeCustomerBuildResults.Add(new OeCustomerBuildResult
            {
                OrganizationId = orgId,
                ReleaseId = releaseId,
                AppName = Truncate(r.AppName, 250),
                AppId = Truncate(r.AppId, 50),
                Status = r.Status,
                Message = r.Message,
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
        var rows = await _db.OeCustomerBuildResults
            .Where(r => r.ReleaseId == releaseId && r.Status == CustomerBuildResultStatus.Compiled)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows) row.Status = CustomerBuildResultStatus.Ingested;
        if (rows.Count > 0) await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Clone ───────────────────────────────────────────────────────────

    private async Task<List<string>> CloneRepositoriesAsync(
        Customer customer, string buildRoot, List<BuildAppResult> results, CancellationToken ct)
    {
        var gitPath = NullIfBlank(Environment.GetEnvironmentVariable("GIT_PATH")) ?? "git";
        var cloneDirs = new List<string>();
        var index = 0;
        foreach (var repo in customer.Repositories)
        {
            var dest = Path.Combine(buildRoot, $"repo-{index++}");
            var pat = await _orgConfig.ResolveRepositoryPatAsync(repo.Provider, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pat))
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, CustomerBuildResultStatus.Failed,
                    $"No {repo.Provider.DisplayName()} access token is set. Add one under Administration → Repositories, then rebuild."));
                continue;
            }

            // The PAT travels as a transient -c http.extraHeader, never in the URL
            // or on disk. GIT_TERMINAL_PROMPT=0 makes a bad/expired token fail fast
            // instead of blocking on an interactive credential prompt.
            var args = new List<string>
            {
                "-c", "http.extraHeader=" + BasicAuthHeaderValue(repo.Provider, pat),
                "clone", "--depth", "1", "--quiet", repo.Url, dest,
            };
            var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
            var result = await _processRunner.RunAsync(new ProcessRunRequest(gitPath, args, buildRoot, env), ct).ConfigureAwait(false);
            if (result.Succeeded && Directory.Exists(dest))
            {
                cloneDirs.Add(dest);
            }
            else
            {
                results.Add(new BuildAppResult(repo.DisplayName, string.Empty, CustomerBuildResultStatus.Failed,
                    $"git clone failed: {Sanitize(result.StdErr, pat)}".Trim()));
                _logger.LogWarning("Customer {CustomerId}: clone of {Repo} exited {Exit}.", customer.Id, repo.DisplayName, result.ExitCode);
            }
        }
        return cloneDirs;
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
    /// (so the customer Release's <c>ParentReleaseId</c> can point at it), importing
    /// it inline from the already-downloaded zips when absent. Best-effort: a failed
    /// parent import logs and returns null rather than sinking the customer build.
    /// </summary>
    private async Task<int?> EnsureParentReleaseAsync(ResolvedArtifact resolved, BcArtifactDownload download, CancellationToken ct)
    {
        var existing = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Label == resolved.Label && r.DeletedAt == null)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        try
        {
            var metadata = new ReleaseImportMetadata(
                Label: resolved.Label, Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null);
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

            _logger.LogInformation("Auto-imported parent BC release {Label} (release {ParentId}) for a customer build.", resolved.Label, parentId);
            return parentId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to auto-import the parent BC release {Label}; the customer build continues without cross-release resolution.",
                resolved.Label);
            return null;
        }
    }

    // ── Compile ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles one extension with <c>alc</c> against <paramref name="symbolsDir"/>,
    /// returning the output <c>.app</c> path or null on failure. The output lands in
    /// the symbol dir so apps later in the order can depend on it.
    /// </summary>
    private async Task<string?> CompileAsync(DiscoveredApp app, string symbolsDir, AlCompilerInfo compiler, CancellationToken ct)
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
        if (result.Succeeded && File.Exists(outFile))
        {
            return outFile;
        }
        _logger.LogWarning("alc failed for {App} (exit {Exit}): {Err}", app.Manifest.Name, result.ExitCode,
            Truncate(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr, 2000));
        return null;
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

    /// <summary>
    /// Returns <paramref name="desired"/> if no other active release in this org
    /// already uses it, else the same with a " (2)", " (3)", … suffix — so a
    /// rebuild of the same customer+version doesn't trip the unique label index.
    /// </summary>
    private async Task<string> PickUniqueLabelAsync(string desired, int releaseId, CancellationToken ct)
    {
        var taken = await _db.OeReleases.AsNoTracking()
            .Where(r => r.DeletedAt == null && r.Id != releaseId && r.Label.StartsWith(desired))
            .Select(r => r.Label)
            .ToListAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(desired)) return desired;
        for (var n = 2; ; n++)
        {
            var candidate = $"{desired} ({n})";
            if (!set.Contains(candidate)) return candidate;
        }
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
            return new AppJsonManifest(id, Str("name"), Str("publisher"), Str("version"), StrOrNull("application"), StrOrNull("platform"), deps);
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

    /// <summary>Normalises the country fallback chain: per-customer → org default → <c>w1</c>.</summary>
    internal static string ResolveCountry(string? customerCountry, string? orgCountry)
    {
        if (!string.IsNullOrWhiteSpace(customerCountry)) return customerCountry.Trim().ToLowerInvariant();
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

    /// <summary>Strips any accidental occurrence of the PAT from a tool's stderr before it's stored/logged.</summary>
    private static string Sanitize(string text, string secret) =>
        string.IsNullOrEmpty(text) ? text
        : (string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***"));

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>A discovered extension: the folder holding its app.json and the parsed manifest.</summary>
public sealed record DiscoveredApp(string ProjectDir, AppJsonManifest Manifest);

/// <summary>The fields the build pipeline reads from an <c>app.json</c>.</summary>
public sealed record AppJsonManifest(
    string Id,
    string Name,
    string Publisher,
    string Version,
    string? Application,
    string? Platform,
    IReadOnlyList<AppJsonDependency> Dependencies);

/// <summary>One inter-app dependency declared in <c>app.json</c> (id + name).</summary>
public sealed record AppJsonDependency(string Id, string Name);

/// <summary>One extension's outcome from a build, before it's persisted as a <see cref="CustomerBuildResult"/> row.</summary>
public sealed record BuildAppResult(string AppName, string AppId, string Status, string? Message);

/// <summary>
/// The product of a customer build: the compiled uploads ready for the shared
/// ingest seam, the per-app report, the resolved parent release (when known), and
/// the finalised Release label.
/// </summary>
public sealed record CustomerBuildOutcome(
    IReadOnlyList<AppFileUpload> Uploads,
    IReadOnlyList<BuildAppResult> Results,
    int? ParentReleaseId,
    string? FinalLabel);
