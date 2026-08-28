using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <inheritdoc cref="IBcAdminClient"/>
public sealed class BcAdminClient : IBcAdminClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcAdminClient> _logger;

    public BcAdminClient(IHttpClientFactory httpFactory, ILogger<BcAdminClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BcConstants.AdminEnvironmentsUrl);
        request.UseBearer(accessToken);

        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BcApiException(null, "Couldn't reach the Business Central Admin Center API.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Read the error envelope, don't just log the status. Microsoft returns a
                // stable `code` and a diagnostic `message` here, and without them a 401 is
                // indistinguishable from a 403 in the logs — which is how "the app isn't on
                // the authorized-apps list" got misread as "GDAP is missing" for a customer
                // who had no GDAP relationship in the first place.
                var detail = ExtractError(body);
                _logger.LogWarning("BC admin environments call returned {Status}. {Detail}",
                    response.StatusCode, detail.Length > 0 ? detail : "(no error body)");
                throw new BcApiException(response.StatusCode,
                    $"The Admin Center API returned {(int)response.StatusCode}. {detail}".TrimEnd());
            }

            return ParseEnvironments(body);
        }
    }

    /// <summary>
    /// Pulls a short, secret-free summary out of an Admin Center error envelope
    /// (<c>{ "code": ..., "message": ... }</c>), falling back to the OData <c>error.message</c>
    /// shape the automation API uses. Empty when the body isn't JSON or carries neither.
    /// </summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            // The automation API nests the same two fields under "error".
            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                root = nested;
            }

            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var detail = (code, message) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
                ({ Length: > 0 }, _) => code!,
                (_, { Length: > 0 }) => message!,
                _ => string.Empty,
            };
            return detail.Length > 300 ? detail[..300] : detail;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Parses the Admin Center <c>{ "value": [ { name, type } ] }</c> envelope. Internal for the client test.</summary>
    internal static IReadOnlyList<BcEnvironment> ParseEnvironments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BcEnvironment>();
        }

        var result = new List<BcEnvironment>();
        foreach (var item in value.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            result.Add(new BcEnvironment(name, type));
        }
        return result;
    }
}
