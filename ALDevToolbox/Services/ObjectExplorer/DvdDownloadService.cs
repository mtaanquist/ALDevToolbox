using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Downloads a Business Central DVD ZIP from a SiteAdmin-allowed URL to a temp
/// file so the release importer can walk it like an uploaded folder ZIP. Lets
/// admins paste Microsoft's multi-GB download link instead of fetching and
/// re-zipping the <c>Applications/</c> folder by hand.
///
/// <para>
/// Two guards apply to the admin-supplied URL: the host must match the
/// SiteAdmin allow-list (<see cref="SystemSettingsService.GetReleaseDownloadAllowedHostsAsync"/>),
/// and the named <see cref="System.Net.Http.HttpClient"/> dials through
/// <see cref="OAuth.SsrfGuard"/> so neither the URL nor any redirect hop can
/// reach an internal address. Redirects are followed (Microsoft download URLs
/// commonly 302 to a CDN) but only the pasted host is allow-list-checked; the
/// SSRF guard is what constrains redirect targets.
/// </para>
/// </summary>
public sealed class DvdDownloadService
{
    /// <summary>Temp-file prefix, mirroring the <c>oe-folder-</c> upload staging convention.</summary>
    public const string TempPrefix = "oe-dvd-";

    /// <summary>
    /// Hard cap on the downloaded body. A first-party DVD ZIP is 1–3 GB; 5 GB
    /// leaves margin while still refusing a runaway/bogus URL before it fills
    /// the container's scratch disk.
    /// </summary>
    public const long MaxDownloadBytes = 5L * 1024 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SystemSettingsService _settings;
    private readonly ILogger<DvdDownloadService> _logger;

    public DvdDownloadService(
        IHttpClientFactory httpClientFactory,
        SystemSettingsService settings,
        ILogger<DvdDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Validates the URL against the allow-list, downloads it to a temp file,
    /// and returns the path. The caller owns the file and must delete it once
    /// the import has consumed it. Throws <see cref="PlanValidationException"/>
    /// (field key <c>DvdUrl</c>) for any user-correctable failure so the form
    /// can surface it inline.
    /// </summary>
    public async Task<string> DownloadToTempAsync(string? url, CancellationToken ct = default)
    {
        var uri = await ValidateUrlAsync(url, ct).ConfigureAwait(false);
        return await DownloadValidatedAsync(uri, ct).ConfigureAwait(false);
    }

    private async Task<string> DownloadValidatedAsync(Uri uri, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N") + ".zip");
        var client = _httpClientFactory.CreateClient(nameof(DvdDownloadService));

        try
        {
            using var response = await client
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Invalid($"The server returned {(int)response.StatusCode} for that URL. Check the link and try again.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dest = File.Create(tempPath);
            await CopyWithCapAsync(source, dest, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Downloaded DVD ZIP from {Host} ({Bytes} bytes) to staging.",
                uri.Host, new FileInfo(tempPath).Length);
            return tempPath;
        }
        catch (PlanValidationException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // SsrfGuard refuses internal targets with IOException; transient
            // network failures land here too. Surface as an inline form error
            // rather than a 500 — the admin can fix the URL or retry.
            TryDelete(tempPath);
            _logger.LogWarning(ex, "DVD download from {Host} failed.", uri.Host);
            throw Invalid("Couldn't download from that URL — it may be unreachable, blocked, or not a public address.");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Validates the URL synchronously (https + allow-listed host) so the
    /// import endpoint can reject a bad URL inline before queuing a job, rather
    /// than surfacing it later as a failed release. Throws
    /// <see cref="PlanValidationException"/> (field key <c>DvdUrl</c>).
    /// </summary>
    public async Task ValidateUrlForQueueAsync(string? url, CancellationToken ct = default) =>
        await ValidateUrlAsync(url, ct).ConfigureAwait(false);

    private async Task<Uri> ValidateUrlAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw Invalid("Enter a download URL.");
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("Enter an absolute https:// URL.");
        }

        var allowlist = await _settings.GetReleaseDownloadAllowedHostsAsync(ct).ConfigureAwait(false);
        if (!SystemSettingsService.IsHostAllowed(uri.Host, allowlist))
        {
            throw Invalid(
                $"'{uri.Host}' isn't on the download allow-list. A SiteAdmin must add it under "
                + "Site administration → Settings before this host can be used.");
        }
        return uri;
    }

    private static async Task CopyWithCapAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw Invalid("That download is larger than the 5 GB limit; it doesn't look like a BC DVD ZIP.");
            }
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static PlanValidationException Invalid(string message) =>
        new(new Dictionary<string, string> { ["DvdUrl"] = message });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* swallow — best-effort cleanup */ }
    }
}
