using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <inheritdoc cref="IBcAutomationClient"/>
public sealed class BcAutomationClient : IBcAutomationClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcAutomationClient> _logger;

    public BcAutomationClient(IHttpClientFactory httpFactory, ILogger<BcAutomationClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BcCompany>> ListCompaniesAsync(
        string accessToken, string environmentName, CancellationToken ct = default)
    {
        var url = $"{EnvironmentBase(environmentName)}/companies";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);
        var body = await SendAsync(request, "listing companies", environmentName, ct).ConfigureAwait(false);
        return ParseCompanies(body);
    }

    public async Task<BcExtensionUpload> CreateExtensionUploadAsync(
        string accessToken, string environmentName, Guid companyId,
        string schedule, string schemaSyncMode, CancellationToken ct = default)
    {
        var url = $"{CompanyBase(environmentName, companyId)}/extensionUpload";
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["schedule"] = schedule,
            ["schemaSyncMode"] = schemaSyncMode,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);
        var body = await SendAsync(request, "creating the extension upload", environmentName, ct).ConfigureAwait(false);
        return ParseExtensionUpload(body);
    }

    public async Task SetExtensionContentAsync(
        string accessToken, string environmentName, Guid companyId,
        string uploadSystemId, byte[] appBytes, CancellationToken ct = default)
    {
        var url = $"{CompanyBase(environmentName, companyId)}/extensionUpload({Key(uploadSystemId)})/extensionContent";
        using var content = new ByteArrayContent(appBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        request.UseBearer(accessToken);
        // The automation API requires If-Match on the content PATCH; "*" means
        // "whatever's there" (we just created the upload, so there's no real ETag race).
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        await SendAsync(request, "uploading the app content", environmentName, ct).ConfigureAwait(false);
    }

    public async Task TriggerExtensionUploadAsync(
        string accessToken, string environmentName, Guid companyId,
        string uploadSystemId, CancellationToken ct = default)
    {
        var url = $"{CompanyBase(environmentName, companyId)}/extensionUpload({Key(uploadSystemId)})/Microsoft.NAV.upload";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // The bound action takes no body, but BC expects a JSON content type.
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.UseBearer(accessToken);
        await SendAsync(request, "starting the install", environmentName, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BcDeploymentStatus>> GetDeploymentStatusAsync(
        string accessToken, string environmentName, Guid companyId, CancellationToken ct = default)
    {
        var url = $"{CompanyBase(environmentName, companyId)}/extensionDeploymentStatus";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);
        var body = await SendAsync(request, "reading the deployment status", environmentName, ct).ConfigureAwait(false);
        return ParseDeploymentStatus(body);
    }

    // ── URL helpers ───────────────────────────────────────────────────────────

    private static string EnvironmentBase(string environmentName) =>
        string.Format(BcConstants.AutomationBaseFormat, Uri.EscapeDataString(environmentName));

    private static string CompanyBase(string environmentName, Guid companyId) =>
        $"{EnvironmentBase(environmentName)}/companies({Key(companyId.ToString())})";

    /// <summary>Formats a GUID OData key segment: BC accepts the bare GUID (no quotes).</summary>
    private static string Key(string systemId) => systemId.Trim('{', '}', '(', ')', '\'');

    // ── Shared send ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the request, maps transport faults and non-success statuses to
    /// <see cref="BcApiException"/> with a secret-free message, and returns the body
    /// (empty for 204). <paramref name="action"/> is a short gerund for the message.
    /// </summary>
    private async Task<string> SendAsync(HttpRequestMessage request, string action, string environmentName, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BcApiException(null, $"Couldn't reach the Business Central automation API while {action}.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BC automation call ({Action}) for environment {Env} returned {Status}.",
                    action, environmentName, response.StatusCode);
                throw new BcApiException(response.StatusCode,
                    $"The automation API returned {(int)response.StatusCode} while {action}. {ExtractError(body)}".TrimEnd());
            }
            return body;
        }
    }

    /// <summary>Pulls the short <c>error.message</c> out of an OData error envelope, if present (secret-free by construction).</summary>
    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                && msg.GetString() is { Length: > 0 } m)
            {
                return m.Length > 300 ? m[..300] : m;
            }
        }
        catch (JsonException) { /* not JSON — ignore */ }
        return string.Empty;
    }

    // ── Parsers (internal for the client tests) ───────────────────────────────

    /// <summary>Parses the automation API <c>{ "value": [ { id, name, displayName } ] }</c> envelope.</summary>
    internal static IReadOnlyList<BcCompany> ParseCompanies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BcCompany>();
        }

        var result = new List<BcCompany>();
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            {
                continue;
            }
            // Prefer the human display name; fall back to the technical name.
            var name = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            }
            result.Add(new BcCompany(id, name ?? id.ToString()));
        }
        return result;
    }

    /// <summary>Reads the <c>systemId</c> of a freshly-created extensionUpload entity.</summary>
    internal static BcExtensionUpload ParseExtensionUpload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var systemId = root.TryGetProperty("systemId", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(systemId) && root.TryGetProperty("id", out var i))
        {
            systemId = i.GetString();
        }
        if (string.IsNullOrWhiteSpace(systemId))
        {
            throw new BcApiException(null, "The extension upload was created but the API didn't return its id.");
        }
        return new BcExtensionUpload(systemId);
    }

    /// <summary>Parses the <c>extensionDeploymentStatus</c> <c>{ "value": [ { name, appVersion, status } ] }</c> envelope.</summary>
    internal static IReadOnlyList<BcDeploymentStatus> ParseDeploymentStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BcDeploymentStatus>();
        }

        var result = new List<BcDeploymentStatus>();
        foreach (var item in value.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var version = item.TryGetProperty("appVersion", out var v) ? v.GetString() ?? string.Empty : string.Empty;
            var status = item.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            result.Add(new BcDeploymentStatus(name, version, status));
        }
        return result;
    }
}
