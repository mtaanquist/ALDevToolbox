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
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BC admin environments call returned {Status}.", response.StatusCode);
                throw new BcApiException(response.StatusCode,
                    $"The Admin Center API returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseEnvironments(body);
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
