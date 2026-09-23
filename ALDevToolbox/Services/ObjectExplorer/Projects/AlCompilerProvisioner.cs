using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using ALDevToolbox.Services.Configuration;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Provisions the AL compiler (<c>alc</c>) at runtime rather than baking it into
/// the image, so a new compiler version never requires an image rebuild. The
/// compiler ships in the <c>Microsoft.Dynamics.BusinessCentral.Development.Tools</c>
/// NuGet package as a framework-dependent <c>tools/&lt;tfm&gt;/any/alc.dll</c>
/// that the host's <c>dotnet</c> runs; this service downloads the <c>.nupkg</c>
/// (a zip), extracts that folder flat into the <c>app-altool</c> volume and
/// records what it installed - no SDK, no <c>dotnet tool install</c> (which
/// rejects these packages). Volumes provisioned before #921 hold the
/// <c>.Linux</c> package's <c>lib/&lt;tfm&gt;/alc</c> apphost instead; that
/// layout is still recognised and run as it was, so an existing install never
/// re-downloads. See <c>.design/object-explorer-project-builds.md</c>.
///
/// <para>
/// Singleton: it owns a shared on-disk resource (the volume) guarded by a
/// semaphore. Provisioning is lazy (first build / admin action), so a NuGet
/// outage never blocks app startup — the feature just reports itself unavailable.
/// </para>
/// </summary>
public sealed class AlCompilerProvisioner
{
    /// <summary>
    /// The AL compiler NuGet package id (lower-cased for the flat-container API).
    /// Not the <c>.Linux</c> package: from 18.x that one carries only the code
    /// analyzers, and the compiler itself is the framework-dependent
    /// <c>alc.dll</c> in this package (#921).
    /// </summary>
    public const string PackageId = "microsoft.dynamics.businesscentral.development.tools";

    /// <summary>
    /// How many versions, newest first, a provisioning pass tries when no pin is
    /// set and a package turns out to carry no compiler. Microsoft has shipped
    /// such packages (#921); a bounded walk finds the newest real one without
    /// turning an odd feed into a forty-download loop.
    /// </summary>
    internal const int MaxCandidates = 3;

    private const string IndexUrl =
        "https://api.nuget.org/v3-flatcontainer/" + PackageId + "/index.json";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlCompilerProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _installDir;
    private readonly string? _versionPin;
    private readonly string? _explicitAlcPath;

    public AlCompilerProvisioner(
        IHttpClientFactory httpFactory,
        ILogger<AlCompilerProvisioner> logger,
        // Optional so hand-built instances keep compiling; DI always supplies
        // the instance registered from configuration.
        AlCompilerOptions? options = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        var compilerOptions = options ?? new AlCompilerOptions();
        _installDir = compilerOptions.InstallDirectory;
        _versionPin = compilerOptions.VersionPin;
        _explicitAlcPath = compilerOptions.ExplicitAlcPath;
    }

    private string BinDir => Path.Combine(_installDir, "bin");
    private string MarkerPath => Path.Combine(_installDir, "installed.json");

    /// <summary>
    /// Ensures a usable <c>alc</c> is present and returns how to invoke it, or
    /// <see langword="null"/> when the compiler can't be provisioned (offline with
    /// an empty volume). Provisions the target version (pin, else newest) on first
    /// use. Safe to call before every build.
    /// </summary>
    public async Task<AlCompilerInfo?> ResolveAsync(CancellationToken ct = default)
    {
        if (_explicitAlcPath is not null)
        {
            return File.Exists(_explicitAlcPath)
                ? new AlCompilerInfo(_explicitAlcPath, NeedsRollForward(_explicitAlcPath), "(pinned path)")
                : null;
        }

        if (Installed() is { } installed) return installed;

        // Nothing installed yet — provision the target version.
        IReadOnlyList<string> candidates;
        try
        {
            candidates = PickCandidates(await FetchVersionsAsync(ct), _versionPin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AL compiler is not installed and NuGet is unreachable; project builds are unavailable.");
            return null;
        }

        foreach (var version in candidates)
        {
            try
            {
                await ProvisionVersionAsync(version, ct).ConfigureAwait(false);
                break;
            }
            catch (AlCompilerPackageException ex) when (_versionPin is null)
            {
                // A package without a compiler, or one the feed lists but will not
                // serve, is a feed quirk, not a fault of ours: say so and try the next older one. A pinned version gets no such
                // leniency - the operator asked for exactly that one.
                _logger.LogWarning("AL compiler package {Version} is not installable ({Reason}); trying the next older version.",
                    version, ex.Message);
            }
        }
        return Installed();
    }

    /// <summary>What the volume holds, in either layout, or null when nothing usable is installed.</summary>
    private AlCompilerInfo? Installed()
    {
        var marker = ReadMarker();
        if (marker is null) return null;
        // Markers written before #921 name no entry: those installs are the
        // .Linux package's apphost.
        var entry = Path.Combine(BinDir, marker.Entry ?? ApphostEntry);
        return File.Exists(entry) ? new AlCompilerInfo(entry, marker.Tfm == "net8.0", marker.Version) : null;
    }

    /// <summary>
    /// Downloads and installs a specific compiler version into the volume,
    /// replacing whatever is there. Used by the admin "Update" action and lazily
    /// by <see cref="ResolveAsync"/>. Serialised by the gate so two builds never
    /// provision at once.
    /// </summary>
    public async Task ProvisionVersionAsync(string version, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: another caller may have just installed it.
            if (ReadMarker()?.Version == version && Installed() is not null)
            {
                return;
            }

            Directory.CreateDirectory(_installDir);
            var lower = version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{lower}/{PackageId}.{lower}.nupkg";

            var http = _httpFactory.CreateClient();
            _logger.LogInformation("Provisioning AL compiler {Version} from NuGet.", version);
            MemoryStream buffer;
            try
            {
                await using var nupkg = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
                buffer = await BufferAsync(nupkg, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Listed in the index, but the blob is gone: an unlisted or
                // half-published version. Not ours to install; the next older one may be.
                throw new AlCompilerPackageException($"AL compiler package {version} is listed but not downloadable (404).", ex);
            }
            using var _ = buffer;
            // alc runs with the app's privileges over attacker-influenced source,
            // so verify the download against NuGet's published SHA-512 before
            // extracting/executing it. AL_COMPILER_VERSION should be pinned in
            // production (the default picks the newest published version). See #429.
            await VerifyPackageHashAsync(http, url, buffer, version, ct).ConfigureAwait(false);
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

            var layout = PickLayout(zip.Entries.Select(e => e.FullName))
                ?? throw new AlCompilerPackageException($"AL compiler package {version} has no tools/<tfm>/any/alc.dll and no lib/<tfm>/alc.");

            // Fresh bin dir, then extract the chosen folder flat into it.
            if (Directory.Exists(BinDir)) Directory.Delete(BinDir, recursive: true);
            Directory.CreateDirectory(BinDir);

            var prefix = layout.Prefix;
            var binRoot = Path.GetFullPath(BinDir) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length <= prefix.Length || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var relative = entry.FullName[prefix.Length..];
                if (relative.EndsWith('/')) continue; // directory entry
                var dest = Path.Combine(BinDir, relative);
                // Zip-slip guard: a package entry with `..` segments could escape
                // BinDir and overwrite process-writable files (the app-keys ring,
                // the backups volume). The package origin is HTTPS-pinned but
                // unverified (no hash/signature, and AL_COMPILER_VERSION can point
                // at any version), so don't trust the entry path. See issue #427.
                var full = Path.GetFullPath(dest);
                if (!full.StartsWith(binRoot, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AL compiler package entry '{entry.FullName}' escapes the install directory.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            // The apphost binaries are extracted without the execute bit.
            foreach (var name in new[] { "alc", "altool" })
            {
                var p = Path.Combine(BinDir, name);
                if (File.Exists(p))
                    File.SetUnixFileMode(p, File.GetUnixFileMode(p)
                        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            if (!File.Exists(Path.Combine(BinDir, layout.Entry)))
                throw new AlCompilerPackageException($"AL compiler package {version} ({prefix}) did not contain '{layout.Entry}'.");

            WriteMarker(new InstalledMarker(version, layout.Tfm, layout.Entry));
            _logger.LogInformation("Installed AL compiler {Version} ({Prefix}{Entry}).", version, prefix, layout.Entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> FetchVersionsAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        await using var stream = await http.GetStreamAsync(IndexUrl, ct).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(e => e.GetString()!).Where(v => v is not null).ToList();
    }

    /// <summary>
    /// Picks the version to install: the pin when set (and present), otherwise the
    /// newest stable release. The NuGet flat-container index lists versions in
    /// SemVer-ascending order, so the newest is the last entry.
    /// </summary>
    public static string? PickNewest(IReadOnlyList<string> versions, string? pin) =>
        PickCandidates(versions, pin).FirstOrDefault();

    /// <summary>
    /// The versions to try, in order: just the pin when one is set (and present),
    /// otherwise the newest <see cref="MaxCandidates"/> stable releases, newest
    /// first, so a package that carries no compiler is skipped for the one before
    /// it. A prerelease is never picked by default - a beta compiler is a choice
    /// an operator makes with <c>AL_COMPILER_VERSION</c> - unless the feed holds
    /// nothing else.
    /// </summary>
    public static IReadOnlyList<string> PickCandidates(IReadOnlyList<string> versions, string? pin)
    {
        if (versions.Count == 0) return [];
        if (!string.IsNullOrWhiteSpace(pin))
        {
            var match = versions.FirstOrDefault(v => string.Equals(v, pin, StringComparison.OrdinalIgnoreCase));
            return match is null ? [] : [match];
        }
        var stable = versions.Where(v => !v.Contains('-')).ToList();
        var pool = stable.Count > 0 ? stable : versions.ToList();
        pool.Reverse();
        return pool.Take(MaxCandidates).ToList();
    }

    /// <summary>The apphost the <c>.Linux</c> package shipped up to 17.x; still what older volumes hold.</summary>
    internal const string ApphostEntry = "alc";

    /// <summary>The framework-dependent compiler the main package ships; run through <c>dotnet</c>.</summary>
    internal const string FrameworkDependentEntry = "alc.dll";

    /// <summary>
    /// Finds the compiler in a package: the framework-dependent
    /// <c>tools/&lt;tfm&gt;/any/alc.dll</c> the main package ships, else the
    /// <c>lib/&lt;tfm&gt;/alc</c> apphost the <c>.Linux</c> package shipped up to
    /// 17.x. Within a layout prefers <c>net10.0</c> (runs natively on the runtime
    /// image), else the highest <c>netN.0</c> (runs with roll-forward). Null when
    /// the package carries no compiler at all - the 18.x <c>.Linux</c> packages
    /// are analyzers only (#921).
    /// </summary>
    public static CompilerLayout? PickLayout(IEnumerable<string> entryNames)
    {
        var names = entryNames.ToList();
        return Find(names, "tools/", 4, FrameworkDependentEntry, p => $"tools/{p}/any/")
            ?? Find(names, "lib/", 3, ApphostEntry, p => $"lib/{p}/");

        static CompilerLayout? Find(List<string> names, string root, int depth, string entry, Func<string, string> prefixOf)
        {
            var tfms = names
                .Where(n => n.StartsWith(root, StringComparison.Ordinal))
                .Select(n => n.Split('/'))
                .Where(p => p.Length == depth && p[1].StartsWith("net", StringComparison.Ordinal) && p[^1] == entry)
                .Select(p => p[1])
                .Distinct()
                .ToList();
            if (tfms.Count == 0) return null;
            // Prefer net10.0; otherwise the highest netN.0 (lexical is wrong for net8 vs net10, so order numerically).
            var tfm = tfms.Contains("net10.0") ? "net10.0" : tfms.OrderByDescending(ParseNetMajor).First();
            return new CompilerLayout(prefixOf(tfm), tfm, entry);
        }
    }

    /// <summary>Retained for callers that only need the framework folder; see <see cref="PickLayout"/>.</summary>
    public static string? PickTfm(IEnumerable<string> entryNames) => PickLayout(entryNames)?.Tfm;

    private static int ParseNetMajor(string tfm)
    {
        var digits = new string(tfm.Skip(3).TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static bool NeedsRollForward(string alcPath)
    {
        // Best-effort for the explicit-path override: a net8 runtimeconfig needs
        // roll-forward on a net10 host. Default to enabling it (harmless on net10).
        return true;
    }

    private InstalledMarker? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath)
                ? JsonSerializer.Deserialize<InstalledMarker>(File.ReadAllText(MarkerPath))
                : null;
        }
        catch { return null; }
    }

    private void WriteMarker(InstalledMarker marker) =>
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));

    private static async Task<MemoryStream> BufferAsync(Stream source, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Verifies the downloaded <c>.nupkg</c> against the base64 SHA-512 NuGet
    /// publishes for it, before the package is extracted and <c>alc</c> is run.
    /// Refuses to install if the hash can't be fetched or doesn't match — without
    /// this a yanked-then-republished or tampered package would become code
    /// execution in the container. See #429.
    ///
    /// <para>The hash comes from the package's registration leaf
    /// (<c>registration5-gz-semver2/{id}/{version}.json</c> → <c>catalogEntry</c>
    /// → <c>packageHash</c>), the same place the NuGet client reads it. The
    /// flat-container <c>.nupkg.sha512</c> resource this used to read answers 404
    /// on nuget.org for every package now, which silently made every fresh
    /// provisioning refuse (#921).</para>
    /// </summary>
    private async Task VerifyPackageHashAsync(
        HttpClient http, string nupkgUrl, MemoryStream content, string version, CancellationToken ct)
    {
        string expected;
        try
        {
            expected = await FetchPublishedHashAsync(http, version, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Downloadable but not registered: a half-published version. Refusing
            // to install is the point; a skippable refusal lets the walk try the
            // version before it.
            throw new AlCompilerPackageException($"AL compiler {version} has no published integrity hash; refusing to install unverified.", ex);
        }
        catch (Exception ex) when (ex is not AlCompilerPackageException)
        {
            throw new InvalidOperationException(
                $"Could not fetch the integrity hash for AL compiler {version}; refusing to install unverified.", ex);
        }

        var actual = Convert.ToBase64String(
            SHA512.HashData(content.GetBuffer().AsSpan(0, (int)content.Length)));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AL compiler {version} failed its SHA-512 integrity check; refusing to install.");
        }
    }

    private const string RegistrationBase = "https://api.nuget.org/v3/registration5-gz-semver2/" + PackageId + "/";

    /// <summary>The base64 SHA-512 from the version's catalog entry. Throws <see cref="AlCompilerPackageException"/> when the entry names no SHA-512.</summary>
    private static async Task<string> FetchPublishedHashAsync(HttpClient http, string version, CancellationToken ct)
    {
        using var leaf = await GetJsonAsync(http, RegistrationBase + version.ToLowerInvariant() + ".json", ct).ConfigureAwait(false);
        var catalogUrl = leaf.RootElement.GetProperty("catalogEntry").GetString()
            ?? throw new AlCompilerPackageException($"AL compiler {version} has no catalog entry in its registration.");
        using var entry = await GetJsonAsync(http, catalogUrl, ct).ConfigureAwait(false);
        var algorithm = entry.RootElement.TryGetProperty("packageHashAlgorithm", out var a) ? a.GetString() : null;
        var hash = entry.RootElement.TryGetProperty("packageHash", out var h) ? h.GetString() : null;
        if (!string.Equals(algorithm, "SHA512", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(hash))
        {
            throw new AlCompilerPackageException($"AL compiler {version} publishes no SHA-512 hash (algorithm '{algorithm}').");
        }
        return hash.Trim();
    }

    /// <summary>Reads a JSON document, inflating it when nuget.org serves it gzip-encoded (the registration resource always does).</summary>
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            await using var inflated = new GZipStream(body, CompressionMode.Decompress);
            return await JsonDocument.ParseAsync(inflated, cancellationToken: ct).ConfigureAwait(false);
        }
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary><paramref name="Entry"/> is null on markers written before #921, which installed the apphost.</summary>
    private sealed record InstalledMarker(string Version, string Tfm, string? Entry = null);
}

/// <summary>Where a package keeps its compiler: the folder to extract flat, its framework, and the file to run.</summary>
public sealed record CompilerLayout(string Prefix, string Tfm, string Entry);

/// <summary>A downloaded package that is not a compiler - the next older version may be.</summary>
public sealed class AlCompilerPackageException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// How to invoke the resolved compiler. <see cref="AlcPath"/> is either the
/// apphost (<c>.../alc</c>, run directly) or the framework-dependent
/// <c>.../alc.dll</c>, which the host's <c>dotnet</c> runs; <see cref="FileName"/>
/// and <see cref="LeadingArguments"/> hide that difference from the build.
/// </summary>
public sealed record AlCompilerInfo(string AlcPath, bool NeedsRollForward, string Version)
{
    public bool IsFrameworkDependent => AlcPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>The process to start: <c>dotnet</c> for a framework-dependent compiler, else the apphost itself.</summary>
    public string FileName => IsFrameworkDependent ? "dotnet" : AlcPath;

    /// <summary>What goes before the compiler's own arguments: the dll path when <c>dotnet</c> hosts it, else nothing.</summary>
    public IReadOnlyList<string> LeadingArguments => IsFrameworkDependent ? [AlcPath] : [];
}
