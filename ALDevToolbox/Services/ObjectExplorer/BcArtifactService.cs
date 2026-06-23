using System.IO.Compression;
using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Resolves and downloads Microsoft Business Central <em>OnPrem</em> artifacts so
/// releases can be imported straight from Microsoft's CDN — no manual DVD
/// download / re-zip. Ports BcContainerHelper's <c>Get-BCArtifactUrl</c> index
/// query (see <see cref="BcArtifactIndex"/>) and reuses
/// <see cref="DvdDownloadService"/>'s resilient, SSRF-guarded download for the
/// multi-GB artifact zips.
///
/// <para>
/// Two surfaces consume this: the per-org auto-import scheduler
/// (<c>ReleaseAutoImportScheduler</c>) and the Artifacts tab on the Import
/// Release page. The Artifacts tab persists the available versions into
/// <c>oe_artifact_versions</c> (<see cref="RefreshIndexAsync"/>) so the table
/// renders from the DB rather than re-querying Azure on every load.
/// </para>
/// </summary>
public sealed class BcArtifactService
{
    /// <summary>Temp-file prefix for staged artifact zips, mirroring the <c>oe-dvd-</c> convention.</summary>
    public const string TempPrefix = "oe-artifact-";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<BcArtifactService> _logger;

    public BcArtifactService(
        IHttpClientFactory httpClientFactory,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<BcArtifactService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; BcArtifactService called outside an authenticated request.");

    // ── Index query ────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the available OnPrem versions for <paramref name="country"/> live
    /// from Microsoft's index, newest first. The raw fetch primitive
    /// <see cref="RefreshIndexAsync"/> and <see cref="ResolveOnPremAsync"/> build on.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableVersionsAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            throw Invalid("Country", "Enter a BC country code, e.g. 'dk' or 'w1'.");
        }

        var countryJson = await GetIndexAsync(BcArtifactIndex.CountryIndexUrl(country), ct).ConfigureAwait(false);
        if (countryJson is null)
        {
            throw Invalid("Country",
                $"No artifact index for country '{country.Trim().ToLowerInvariant()}'. Check the code (e.g. 'dk', 'w1').");
        }
        // Platform index is best-effort: if it's unavailable we don't drop
        // application versions for lack of a cross-check.
        var platformJson = await GetIndexAsync(BcArtifactIndex.PlatformIndexUrl(), ct).ConfigureAwait(false);
        return BcArtifactIndex.ParseVersions(countryJson, platformJson);
    }

    /// <summary>
    /// True when <paramref name="country"/> appears in Microsoft's OnPrem
    /// countries index. Returns <see langword="true"/> when the index can't be
    /// read (don't block on a transient Azure hiccup) — the actual import would
    /// still fail loudly on a bad code.
    /// </summary>
    public async Task<bool> IsKnownCountryAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return false;
        var json = await GetIndexAsync(BcArtifactIndex.CountriesIndexUrl(), ct).ConfigureAwait(false);
        if (json is null) return true;
        var countries = BcArtifactIndex.ParseCountries(json);
        return countries.Count == 0 || countries.Contains(country.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Resolves a version (newest when <paramref name="version"/> is null, else
    /// the exact / Major.Minor match) to its application-artifact URL plus the
    /// derived release label. Returns <see langword="null"/> when nothing matches.
    /// </summary>
    public async Task<ResolvedArtifact?> ResolveOnPremAsync(string country, string? version, CancellationToken ct = default)
    {
        var available = await ListAvailableVersionsAsync(country, ct).ConfigureAwait(false);
        var selected = BcArtifactIndex.SelectVersion(available, version);
        if (selected is null) return null;
        return new ResolvedArtifact(
            Version: selected,
            ApplicationUrl: BcArtifactIndex.BuildApplicationUrl(selected, country),
            Label: BcArtifactIndex.FormatLabel(selected, country),
            MajorMinor: BcArtifactIndex.ToMajorMinor(selected));
    }

    // ── Persisted cache (Artifacts tab) ────────────────────────────────

    /// <summary>
    /// Re-fetches the index for <paramref name="country"/> and upserts the
    /// per-version rows into <c>oe_artifact_versions</c> for the current org,
    /// removing rows for that country that are no longer offered. Returns the
    /// refreshed rows newest-first.
    /// </summary>
    public async Task<IReadOnlyList<BcArtifactVersion>> RefreshIndexAsync(string country, CancellationToken ct = default)
    {
        var normalized = country.Trim().ToLowerInvariant();
        var versions = await ListAvailableVersionsAsync(normalized, ct).ConfigureAwait(false);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var existing = await _db.OeArtifactVersions
            .Where(a => a.Country == normalized)
            .ToListAsync(ct).ConfigureAwait(false);
        var byVersion = existing.ToDictionary(a => a.Version, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(versions, StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            if (byVersion.TryGetValue(version, out var row))
            {
                row.ApplicationUrl = BcArtifactIndex.BuildApplicationUrl(version, normalized);
                row.MajorMinor = BcArtifactIndex.ToMajorMinor(version);
                row.RefreshedAt = now;
            }
            else
            {
                _db.OeArtifactVersions.Add(new BcArtifactVersion
                {
                    OrganizationId = orgId,
                    Country = normalized,
                    Version = version,
                    MajorMinor = BcArtifactIndex.ToMajorMinor(version),
                    ApplicationUrl = BcArtifactIndex.BuildApplicationUrl(version, normalized),
                    RefreshedAt = now,
                });
            }
        }

        // Drop versions Microsoft no longer offers for this country so the table
        // mirrors the live index.
        foreach (var row in existing)
        {
            if (!wanted.Contains(row.Version)) _db.OeArtifactVersions.Remove(row);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Refreshed BC artifact index for org {OrgId}, country {Country}: {Count} version(s).",
            orgId, normalized, versions.Count);

        return await GetCachedVersionsAsync(normalized, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the cached artifact versions for the current org + country, newest first.</summary>
    public async Task<IReadOnlyList<BcArtifactVersion>> GetCachedVersionsAsync(string country, CancellationToken ct = default)
    {
        var normalized = country.Trim().ToLowerInvariant();
        var rows = await _db.OeArtifactVersions
            .AsNoTracking()
            .Where(a => a.Country == normalized)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .OrderByDescending(a => Version.TryParse(a.Version, out var v) ? v : new Version(0, 0))
            .ThenByDescending(a => a.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Download (two-part: application + platform) ─────────────────────

    /// <summary>
    /// Downloads the application artifact at <paramref name="applicationUrl"/>,
    /// reads its <c>manifest.json</c> for the paired platform artifact, and
    /// downloads that too. Returns the staged temp-zip paths; the caller (the
    /// import worker) walks both with <see cref="ReleaseZipStaging"/> and deletes
    /// the temp files once the importer has consumed them.
    /// </summary>
    public async Task<BcArtifactDownload> DownloadArtifactSetAsync(string applicationUrl, CancellationToken ct = default)
    {
        var appZip = await DownloadZipAsync(applicationUrl, ct).ConfigureAwait(false);
        string? platformZip = null;
        try
        {
            var platformUrl = TryReadPlatformUrl(appZip);
            if (platformUrl is not null)
            {
                if (!Uri.TryCreate(platformUrl, UriKind.Absolute, out var platformUri)
                    || !BcArtifactIndex.IsTrustedArtifactHost(platformUri.Host))
                {
                    _logger.LogWarning(
                        "Artifact manifest platformUrl host '{Url}' isn't a trusted Microsoft artifact host; importing the application artifact alone.",
                        platformUrl);
                }
                else
                {
                    platformZip = await DownloadZipAsync(platformUrl, ct).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogWarning("Artifact at {Url} had no platformUrl in its manifest; importing the application artifact alone.", applicationUrl);
            }
        }
        catch
        {
            TryDelete(appZip);
            throw;
        }

        return new BcArtifactDownload(appZip, platformZip);
    }

    private async Task<string> DownloadZipAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("Artifacts", "The artifact URL is not a valid https URL.");
        }
        if (!BcArtifactIndex.IsTrustedArtifactHost(uri.Host))
        {
            throw Invalid("Artifacts", $"'{uri.Host}' isn't a recognised Microsoft artifact host.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".zip");
        // Reuse the DVD client: same SSRF guard, redirect handling, and
        // pooled-connection recycling tuned for large CDN bodies.
        var client = _httpClientFactory.CreateClient(nameof(DvdDownloadService));
        try
        {
            await using var dest = File.Create(tempPath);
            var bytes = await DvdDownloadService.CopyWithRetriesAsync(
                client, uri, dest,
                DvdDownloadService.MaxDownloadAttempts,
                DvdDownloadService.IdleReadTimeout,
                _logger, ct).ConfigureAwait(false);
            _logger.LogInformation("Downloaded BC artifact from {Host} ({Bytes} bytes).", uri.Host, bytes);
            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Opens the staged application zip and reads <c>platformUrl</c> from its <c>manifest.json</c>.</summary>
    private static string? TryReadPlatformUrl(string appZipPath)
    {
        using var archive = ZipFile.OpenRead(appZipPath);
        var manifest = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifest is null) return null;
        using var reader = new StreamReader(manifest.Open());
        return BcArtifactIndex.ReadPlatformUrl(reader.ReadToEnd());
    }

    /// <summary>
    /// GETs an index JSON, returning the body or <see langword="null"/> on 404 /
    /// 403 (not published for this country/type). Transient failures bubble as
    /// <see cref="HttpRequestException"/> for the caller to surface/retry.
    /// </summary>
    private async Task<string?> GetIndexAsync(string url, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(DvdDownloadService));
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static PlanValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}

/// <summary>A resolved OnPrem artifact: the selected version, its download URL, and the derived release label.</summary>
public sealed record ResolvedArtifact(string Version, string ApplicationUrl, string Label, string MajorMinor);

/// <summary>The staged temp-zip paths for a downloaded artifact set; <see cref="PlatformZipPath"/> is null when the artifact had no platform pairing.</summary>
public sealed record BcArtifactDownload(string ApplicationZipPath, string? PlatformZipPath);
