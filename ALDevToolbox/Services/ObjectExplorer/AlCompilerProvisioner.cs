using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Provisions the AL compiler (<c>alc</c>) at runtime rather than baking it into
/// the image, so a new compiler version never requires an image rebuild. The
/// compiler ships in the <c>Microsoft.Dynamics.BusinessCentral.Development.Tools.Linux</c>
/// NuGet package as <c>lib/&lt;tfm&gt;/alc</c>; this service downloads the
/// <c>.nupkg</c> (a zip), extracts the right target-framework folder into the
/// <c>app-altool</c> volume, and marks the execute bit — no SDK, no
/// <c>dotnet tool install</c> (which rejects these packages because they ship
/// under <c>lib/</c> not <c>tools/</c>). See
/// <c>.design/object-explorer-project-builds.md</c>.
///
/// <para>
/// Singleton: it owns a shared on-disk resource (the volume) guarded by a
/// semaphore. Provisioning is lazy (first build / admin action), so a NuGet
/// outage never blocks app startup — the feature just reports itself unavailable.
/// </para>
/// </summary>
public sealed class AlCompilerProvisioner
{
    /// <summary>The cross-platform AL compiler NuGet package id (lower-cased for the flat-container API).</summary>
    public const string PackageId = "microsoft.dynamics.businesscentral.development.tools.linux";

    private const string IndexUrl =
        "https://api.nuget.org/v3-flatcontainer/" + PackageId + "/index.json";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlCompilerProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _installDir;
    private readonly string? _versionPin;
    private readonly string? _explicitAlcPath;

    public AlCompilerProvisioner(IHttpClientFactory httpFactory, ILogger<AlCompilerProvisioner> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _installDir = Environment.GetEnvironmentVariable("AL_COMPILER_DIR") ?? "/var/lib/aldevtoolbox/altool";
        _versionPin = NullIfBlank(Environment.GetEnvironmentVariable("AL_COMPILER_VERSION"));
        _explicitAlcPath = NullIfBlank(Environment.GetEnvironmentVariable("AL_COMPILER_PATH"));
    }

    private string BinDir => Path.Combine(_installDir, "bin");
    private string MarkerPath => Path.Combine(_installDir, "installed.json");

    /// <summary>
    /// Returns the status surfaced on the admin UI: which compiler version is
    /// installed, the newest available on NuGet, and whether an update is
    /// available. Network failures degrade to "newest unknown" rather than throw.
    /// </summary>
    public async Task<AlCompilerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (_explicitAlcPath is not null)
        {
            var present = File.Exists(_explicitAlcPath);
            return new AlCompilerStatus(present, present ? "(pinned path)" : null, null, false,
                present ? null : $"AL_COMPILER_PATH points at '{_explicitAlcPath}', which doesn't exist.");
        }

        var installed = ReadMarker()?.Version;
        string? newest = null;
        string? message = null;
        try
        {
            newest = PickNewest(await FetchVersionsAsync(ct), _versionPin);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query NuGet for the newest AL compiler version.");
            message = "Couldn't reach NuGet to check for a newer compiler.";
        }

        var updateAvailable = installed is not null && newest is not null
            && !string.Equals(installed, newest, StringComparison.OrdinalIgnoreCase);
        return new AlCompilerStatus(
            Available: installed is not null,
            InstalledVersion: installed,
            NewestVersion: newest,
            UpdateAvailable: updateAvailable,
            Message: message);
    }

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

        var marker = ReadMarker();
        if (marker is not null && File.Exists(Path.Combine(BinDir, "alc")))
        {
            return new AlCompilerInfo(Path.Combine(BinDir, "alc"), marker.Tfm == "net8.0", marker.Version);
        }

        // Nothing installed yet — provision the target version.
        string? target;
        try
        {
            target = PickNewest(await FetchVersionsAsync(ct), _versionPin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AL compiler is not installed and NuGet is unreachable; project builds are unavailable.");
            return null;
        }
        if (target is null) return null;

        await ProvisionVersionAsync(target, ct).ConfigureAwait(false);
        var m = ReadMarker();
        return m is not null
            ? new AlCompilerInfo(Path.Combine(BinDir, "alc"), m.Tfm == "net8.0", m.Version)
            : null;
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
            if (ReadMarker()?.Version == version && File.Exists(Path.Combine(BinDir, "alc")))
            {
                return;
            }

            Directory.CreateDirectory(_installDir);
            var lower = version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{lower}/{PackageId}.{lower}.nupkg";

            var http = _httpFactory.CreateClient();
            _logger.LogInformation("Provisioning AL compiler {Version} from NuGet.", version);
            await using var nupkg = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
            using var buffer = await BufferAsync(nupkg, ct).ConfigureAwait(false);
            // alc runs with the app's privileges over attacker-influenced source,
            // so verify the download against NuGet's published SHA-512 before
            // extracting/executing it. AL_COMPILER_VERSION should be pinned in
            // production (the default picks the newest published version). See #429.
            await VerifyPackageHashAsync(http, url, buffer, version, ct).ConfigureAwait(false);
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

            var tfm = PickTfm(zip.Entries.Select(e => e.FullName));
            if (tfm is null)
                throw new InvalidOperationException($"AL compiler package {version} has no usable lib/<tfm>/ folder.");

            // Fresh bin dir, then extract the chosen tfm folder flat into it.
            if (Directory.Exists(BinDir)) Directory.Delete(BinDir, recursive: true);
            Directory.CreateDirectory(BinDir);

            var prefix = $"lib/{tfm}/";
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

            if (!File.Exists(Path.Combine(BinDir, "alc")))
                throw new InvalidOperationException($"AL compiler package {version} (lib/{tfm}/) did not contain an 'alc' binary.");

            WriteMarker(new InstalledMarker(version, tfm));
            _logger.LogInformation("Installed AL compiler {Version} ({Tfm}).", version, tfm);
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
    /// newest. The NuGet flat-container index lists versions in SemVer-ascending
    /// order, so the newest — including prerelease — is the last entry.
    /// </summary>
    public static string? PickNewest(IReadOnlyList<string> versions, string? pin)
    {
        if (versions.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(pin))
        {
            return versions.FirstOrDefault(v => string.Equals(v, pin, StringComparison.OrdinalIgnoreCase));
        }
        return versions[^1];
    }

    /// <summary>
    /// Chooses the target-framework folder to extract from the package: prefer
    /// <c>net10.0</c> (runs natively on the runtime image), else the newest
    /// <c>net8.0</c>-style folder (runs with roll-forward). Returns the bare tfm
    /// (e.g. <c>net10.0</c>) or null when no <c>lib/netX/</c> folder exists.
    /// </summary>
    public static string? PickTfm(IEnumerable<string> entryNames)
    {
        var tfms = entryNames
            .Where(n => n.StartsWith("lib/", StringComparison.Ordinal))
            .Select(n => n.Split('/'))
            .Where(p => p.Length >= 3 && p[1].StartsWith("net", StringComparison.Ordinal))
            .Select(p => p[1])
            .Distinct()
            .ToList();
        if (tfms.Count == 0) return null;
        if (tfms.Contains("net10.0")) return "net10.0";
        // Otherwise the highest netN.0 (lexical is wrong for net8 vs net10, so order numerically).
        return tfms.OrderByDescending(ParseNetMajor).First();
    }

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
    /// publishes at the flat-container <c>.nupkg.sha512</c> resource, before the
    /// package is extracted and <c>alc</c> is run. Refuses to install if the hash
    /// can't be fetched or doesn't match — without this a yanked-then-republished
    /// or tampered package would become code execution in the container. See #429.
    /// </summary>
    private async Task VerifyPackageHashAsync(
        HttpClient http, string nupkgUrl, MemoryStream content, string version, CancellationToken ct)
    {
        string expected;
        try
        {
            expected = (await http.GetStringAsync(nupkgUrl + ".sha512", ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex)
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

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record InstalledMarker(string Version, string Tfm);
}

/// <summary>Admin-facing compiler status: installed + newest-available + update flag.</summary>
public sealed record AlCompilerStatus(
    bool Available,
    string? InstalledVersion,
    string? NewestVersion,
    bool UpdateAvailable,
    string? Message);

/// <summary>How to invoke the resolved compiler: the <c>alc</c> path and whether it needs roll-forward.</summary>
public sealed record AlCompilerInfo(string AlcPath, bool NeedsRollForward, string Version);
