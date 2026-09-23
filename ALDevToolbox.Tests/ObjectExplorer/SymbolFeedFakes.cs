using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Synthetic Business Central <c>.app</c> files: the 40-byte NAVX prefix and a zip
/// holding a <c>NavxManifest.xml</c>. Enough for anything that reads an app's
/// identity; there is no symbol tree behind it.
/// </summary>
internal static class SyntheticApp
{
    public static byte[] Build(string appId, string name, string publisher, string version,
        IReadOnlyList<(string Id, string Name, string Version)>? dependencies = null)
    {
        byte[] zipBytes;
        using (var zipMs = new MemoryStream())
        {
            using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var deps = new StringBuilder();
                foreach (var d in dependencies ?? [])
                {
                    deps.Append($"<Dependency Id=\"{d.Id}\" Name=\"{d.Name}\" Publisher=\"Test\" MinVersion=\"{d.Version}\" />");
                }
                using var w = new StreamWriter(zip.CreateEntry("NavxManifest.xml").Open());
                w.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
                    + $"<App Id=\"{appId}\" Name=\"{WebUtility.HtmlEncode(name)}\" Publisher=\"{WebUtility.HtmlEncode(publisher)}\" Version=\"{version}\" />"
                    + "<ResourceExposurePolicy />"
                    + $"<Dependencies>{deps}</Dependencies>"
                    + "</Package>");
            }
            zipBytes = zipMs.ToArray();
        }
        var result = new byte[40 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        Buffer.BlockCopy(zipBytes, 0, result, 40, zipBytes.Length);
        return result;
    }
}

/// <summary>
/// An in-memory stand-in for Microsoft's symbol feeds, shaped like the real ones
/// as issue #901 recorded them: a NuGet v3 service index, a search that reports
/// <c>totalHits: 0</c> while returning data, a flat container whose version index
/// runs newest first, a <c>.nupkg</c> that answers 303 to a blob host, and a
/// package with its <c>.app</c> at the root beside a nuspec that names further
/// dependencies by app id.
/// </summary>
internal sealed class FakeSymbolFeeds : HttpMessageHandler, IHttpClientFactory
{
    public const string AppSourceIndex = "https://feeds.test/appsource/index.json";
    public const string MicrosoftIndex = "https://feeds.test/mssymbols/index.json";

    private readonly Dictionary<string, List<FakePackage>> _feeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appsource"] = new(),
        ["mssymbols"] = new(),
    };

    /// <summary>Feeds that answer every request with a 503.</summary>
    public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every URL requested, in order.</summary>
    public List<string> Requests { get; } = new();

    /// <summary>Anything this handler does not recognise (e.g. an artifact CDN in a build test).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Fallback { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public FakePackage Add(string feed, string packageId, string appId, string name, string version,
        string? application = null, IReadOnlyList<(string PackageId, string Version)>? dependsOn = null)
    {
        var package = new FakePackage(packageId, appId, name, version, application, dependsOn ?? []);
        _feeds[feed].Add(package);
        return package;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.ToString());
        if (uri.Host == "blob.test")
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var package = _feeds[parts[0]].Single(p =>
                p.PackageId.Equals(parts[1], StringComparison.OrdinalIgnoreCase) && p.Version == parts[2]);
            return Task.FromResult(Bytes(package.Nupkg()));
        }
        if (uri.Host != "feeds.test")
        {
            return Task.FromResult(Fallback?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var feed = segments[0];
        if (Down.Contains(feed)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var packages = _feeds[feed];

        if (segments[1] == "index.json")
        {
            return Task.FromResult(Json(new
            {
                version = "3.0.0",
                resources = new object[]
                {
                    new Dictionary<string, string> { ["@id"] = $"https://feeds.test/{feed}/query2/", ["@type"] = "SearchQueryService/3.0.0-beta" },
                    new Dictionary<string, string> { ["@id"] = $"https://feeds.test/{feed}/flat2/", ["@type"] = "PackageBaseAddress/3.0.0" },
                },
            }));
        }
        if (segments[1] == "query2")
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query)["q"] ?? string.Empty;
            var hits = packages.Where(p => p.PackageId.EndsWith(q, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { id = g.Key, version = g.First().Version })
                .ToList();
            // The Azure DevOps quirk: totalHits says nothing was found.
            return Task.FromResult(Json(new { totalHits = "0", data = hits }));
        }
        if (segments[1] == "flat2")
        {
            var id = segments[2];
            var versions = packages.Where(p => p.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (versions.Count == 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (segments[3] == "index.json")
            {
                // Newest first, as the real feed lists them.
                return Task.FromResult(Json(new
                {
                    versions = versions.OrderByDescending(p => Version.Parse(p.Version)).Select(p => p.Version).ToArray(),
                }));
            }
            var package = versions.Single(p => p.Version == segments[3]);
            if (segments[4].EndsWith(".nuspec", StringComparison.Ordinal))
            {
                return Task.FromResult(Bytes(Encoding.UTF8.GetBytes(package.Nuspec())));
            }
            var redirect = new HttpResponseMessage(HttpStatusCode.SeeOther);
            redirect.Headers.Location = new Uri($"https://blob.test/{feed}/{package.PackageId}/{package.Version}");
            return Task.FromResult(redirect);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
}

internal sealed record FakePackage(
    string PackageId, string AppId, string Name, string Version, string? Application,
    IReadOnlyList<(string PackageId, string Version)> DependsOn)
{
    public string AppFileName => $"Test_{Name.Replace(" ", string.Empty)}_{Version}.app";

    public string Nuspec()
    {
        var deps = new StringBuilder();
        if (Application is not null)
        {
            deps.Append($"<dependency id=\"Microsoft.Application.symbols\" version=\"{Application}\" />");
            deps.Append($"<dependency id=\"Microsoft.Platform.symbols\" version=\"{Application}\" />");
        }
        foreach (var (id, version) in DependsOn)
        {
            deps.Append($"<dependency id=\"{id}\" version=\"{version}\" />");
        }
        return "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata>"
            + $"<id>{PackageId}</id><version>{Version}</version><title>{Name}</title>"
            + $"<dependencies>{deps}</dependencies>"
            + "</metadata></package>";
    }

    public byte[] Nupkg()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var app = zip.CreateEntry(AppFileName).Open())
            {
                app.Write(SyntheticApp.Build(AppId, Name, "Test", Version));
            }
            using (var w = new StreamWriter(zip.CreateEntry(PackageId + ".nuspec").Open()))
            {
                w.Write(Nuspec());
            }
            using (var w = new StreamWriter(zip.CreateEntry(".signature.p7s").Open()))
            {
                w.Write("not a real signature");
            }
        }
        return ms.ToArray();
    }
}
