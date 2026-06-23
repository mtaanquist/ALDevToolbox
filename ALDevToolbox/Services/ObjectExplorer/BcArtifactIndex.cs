using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Pure parsing / formatting helpers for Microsoft's Business Central artifact
/// index — the logic ported from BcContainerHelper's <c>Get-BCArtifactUrl</c> /
/// <c>QueryArtifactsFromIndex</c>. Kept free of HTTP and the database so the
/// version-selection and URL/label rules are unit-testable against a captured
/// index sample. <see cref="BcArtifactService"/> owns the network and DB sides.
///
/// <para>
/// Microsoft serves a small JSON index per type+country at
/// <c>https://{host}/{type}/indexes/{country}.json</c> — an array of records
/// each carrying a <c>Version</c> and a <c>CreationTime</c>. A sibling
/// <c>platform.json</c> lists the versions that also have a platform artifact;
/// an application build is only usable when its version appears there too.
/// </para>
/// </summary>
public static class BcArtifactIndex
{
    /// <summary>Only OnPrem artifacts ship loose <c>.app</c> files the Object Explorer can walk (see CLAUDE.md / the plan).</summary>
    public const string OnPremType = "onprem";

    /// <summary>
    /// Oldest major we offer for import. BC 15 (Oct 2019) is the first release
    /// to run the AL-on-server architecture and ship loose <c>.app</c> files;
    /// everything below it is C/AL, a different runtime the AL walker can't read
    /// (the C/AL TXT ingest path is a separate, manual flow). Indexes still list
    /// the 14.x-and-older builds, so we floor them out here.
    /// </summary>
    public const int MinimumAlMajor = 15;

    /// <summary>
    /// Azure blob host BcContainerHelper nominally resolves to. Microsoft has
    /// since disabled anonymous access to it (it returns 403), so we don't fetch
    /// from it — it's kept as the rewrite source for <see cref="ToCdnUrl"/> and
    /// as a trusted host for <see cref="IsTrustedArtifactHost"/>.
    /// </summary>
    public const string BlobHost = "bcartifacts.blob.core.windows.net";

    /// <summary>The Front Door host the index and downloads are actually served from.</summary>
    public const string DefaultCdnHost = "bcartifacts-exdbf9fwegejdqak.b02.azurefd.net";

    /// <summary>
    /// Front Door CDN host for the index and download URLs. Defaults to
    /// <see cref="DefaultCdnHost"/>; overridable via the <c>BC_ARTIFACT_CDN_HOST</c>
    /// env var so a Microsoft rotation of the opaque Front Door identifier is a
    /// config change + restart rather than a rebuild. There is no dynamic
    /// discovery — BcContainerHelper itself hardcodes the same value.
    /// </summary>
    public static readonly string CdnHost = ResolveCdnHost();

    private static string ResolveCdnHost()
    {
        var fromEnv = Environment.GetEnvironmentVariable("BC_ARTIFACT_CDN_HOST");
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultCdnHost : fromEnv.Trim();
    }

    /// <summary>URL of the per-country index for the OnPrem type.</summary>
    public static string CountryIndexUrl(string country) =>
        $"https://{CdnHost}/{OnPremType}/indexes/{country.Trim().ToLowerInvariant()}.json";

    /// <summary>URL of the platform index for the OnPrem type.</summary>
    public static string PlatformIndexUrl() =>
        $"https://{CdnHost}/{OnPremType}/indexes/platform.json";

    /// <summary>URL of the countries index for the OnPrem type (used to validate a configured country).</summary>
    public static string CountriesIndexUrl() =>
        $"https://{CdnHost}/{OnPremType}/indexes/countries.json";

    /// <summary>
    /// Parses a country index (and optional platform index) into the available
    /// versions, newest first. When <paramref name="platformJson"/> is supplied,
    /// application versions without a matching platform artifact are dropped —
    /// they can't be downloaded into a walkable set. Unparseable version strings
    /// are skipped rather than throwing.
    /// </summary>
    public static IReadOnlyList<string> ParseVersions(string countryJson, string? platformJson)
    {
        var countryVersions = ReadVersions(countryJson);

        HashSet<string>? platformVersions = null;
        if (!string.IsNullOrWhiteSpace(platformJson))
        {
            platformVersions = new HashSet<string>(ReadVersions(platformJson), StringComparer.OrdinalIgnoreCase);
        }

        return countryVersions
            .Where(v => platformVersions is null || platformVersions.Contains(v))
            .Where(IsAlEra)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ToComparableVersion)
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Picks a version from <paramref name="available"/> (assumed newest-first):
    /// <paramref name="requested"/> <see langword="null"/> returns the newest;
    /// a full four-part version returns the exact match; a <c>Major.Minor</c>
    /// prefix returns the newest build of that minor. Returns <see langword="null"/>
    /// when nothing matches.
    /// </summary>
    public static string? SelectVersion(IReadOnlyList<string> available, string? requested)
    {
        if (available.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(requested)) return available[0];

        var trimmed = requested.Trim();
        var exact = available.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Treat the request as a Major.Minor (or any leading-segment) prefix and
        // pick the newest matching build — `available` is already newest-first.
        var prefix = trimmed.EndsWith('.') ? trimmed : trimmed + ".";
        return available.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Major.Minor of a dotted version, e.g. <c>28.2.50931.51727</c> → <c>28.2</c>. Falls back to the raw string when it has fewer than two segments.</summary>
    public static string ToMajorMinor(string version)
    {
        var segments = (version ?? string.Empty).Split('.');
        return segments.Length >= 2 ? $"{segments[0]}.{segments[1]}" : (version ?? string.Empty);
    }

    /// <summary>
    /// The auto-import / artifact release label: "Business Central {Major}.{Minor} ({CC})",
    /// e.g. <c>Business Central 28.2 (DK)</c>. The country code is upper-cased.
    /// </summary>
    public static string FormatLabel(string version, string country) =>
        $"Business Central {ToMajorMinor(version)} ({country.Trim().ToUpperInvariant()})";

    /// <summary>
    /// Builds the application-artifact download URL on the CDN host, e.g.
    /// <c>https://{cdn}/onprem/28.2.50931.51727/dk</c> — the shape
    /// <c>Get-BCArtifactUrl</c> returns.
    /// </summary>
    public static string BuildApplicationUrl(string version, string country) =>
        $"https://{CdnHost}/{OnPremType}/{version}/{country.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Rewrites a Microsoft artifact URL onto the active <see cref="CdnHost"/>.
    /// The manifest's <c>platformUrl</c> comes back pointing at the (now 403-ing)
    /// blob host — or could point at a stale Front Door host — so we normalise it
    /// onto the host we know serves anonymously before downloading. Mirrors
    /// BcContainerHelper's <c>ReplaceCDN</c>. URLs on a non-bcartifacts host (or
    /// already on <see cref="CdnHost"/>) are returned unchanged.
    /// </summary>
    public static string ToCdnUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        var host = uri.Host.ToLowerInvariant();
        if (host == CdnHost.ToLowerInvariant()) return url;

        // Only rewrite recognised Microsoft artifact hosts; anything else is left
        // alone (and would be refused by IsTrustedArtifactHost on download).
        var isArtifactHost = host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal)
            || host.EndsWith(".azureedge.net", StringComparison.Ordinal)
            || host.EndsWith(".azurefd.net", StringComparison.Ordinal);
        if (!isArtifactHost) return url;

        return $"{uri.Scheme}://{CdnHost}{uri.PathAndQuery}";
    }

    /// <summary>
    /// Derives the platform-artifact URL from an application-artifact URL by
    /// convention: replace the trailing country segment with the literal
    /// <c>platform</c> (e.g. <c>…/onprem/28.2.50931.51034/dk</c> →
    /// <c>…/onprem/28.2.50931.51034/platform</c>). Mirrors BcContainerHelper's
    /// <c>Download-Artifacts</c> fallback for when the application manifest
    /// carries no <c>platformUrl</c> (the common case for these artifacts).
    /// Returns <see langword="null"/> if the URL can't be parsed.
    /// </summary>
    public static string? DerivePlatformUrl(string applicationUrl)
    {
        if (!Uri.TryCreate(applicationUrl, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0) return null;
        return $"{uri.Scheme}://{uri.Host}{path[..lastSlash]}/platform";
    }

    /// <summary>
    /// True when <paramref name="host"/> is one of Microsoft's fixed artifact
    /// hosts. Used to vet both the URLs we build and the <c>platformUrl</c> we
    /// read out of a downloaded manifest before fetching it, so a tampered
    /// manifest can't redirect the download elsewhere. The SSRF guard on the
    /// HttpClient is the second layer.
    /// </summary>
    public static bool IsTrustedArtifactHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().ToLowerInvariant();
        return string.Equals(host, BlobHost, StringComparison.Ordinal)
            || string.Equals(host, CdnHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".azurefd.net", StringComparison.Ordinal)
            || host.EndsWith(".azureedge.net", StringComparison.Ordinal)
            || host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal);
    }

    /// <summary>Reads the <c>platformUrl</c> string out of an artifact's <c>manifest.json</c> body, or null when absent/unparseable.</summary>
    public static string? ReadPlatformUrl(string manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "platformUrl", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var url = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(url) ? null : url;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to null — the caller imports the application artifact alone.
        }
        return null;
    }

    /// <summary>Parses the countries index into the set of available country codes (lower-cased).</summary>
    public static IReadOnlyCollection<string> ParseCountries(string countriesJson)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(countriesJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(countriesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var c = el.GetString();
                        if (!string.IsNullOrWhiteSpace(c)) result.Add(c.Trim().ToLowerInvariant());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Treat an unparseable countries index as "unknown" — callers skip validation.
        }
        return result;
    }

    /// <summary>
    /// Reads the <c>Version</c> string off each record in an index array. The
    /// records also carry <c>CreationTime</c>, which we don't need for selection
    /// (the version itself sorts deterministically). Tolerates a bare string
    /// array too.
    /// </summary>
    private static IEnumerable<string> ReadVersions(string json)
    {
        var versions = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return versions;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return versions; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return versions;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) versions.Add(s);
                        break;
                    case JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, "Version", StringComparison.OrdinalIgnoreCase)
                                && prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var v = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(v)) versions.Add(v);
                                break;
                            }
                        }
                        break;
                }
            }
        }
        return versions;
    }

    /// <summary>Parses a dotted version for ordering; unparseable strings sort last.</summary>
    private static Version ToComparableVersion(string version) =>
        Version.TryParse(version, out var v) ? v : new Version(0, 0);

    /// <summary>
    /// True when the version's major is <see cref="MinimumAlMajor"/> or newer —
    /// i.e. an AL-on-server build the walker can consume. Unparseable or
    /// majorless versions are excluded.
    /// </summary>
    private static bool IsAlEra(string version)
    {
        var major = version?.Split('.', 2)[0];
        return int.TryParse(major, out var n) && n >= MinimumAlMajor;
    }
}
