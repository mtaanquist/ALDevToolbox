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
        var url = $"{string.Format(BcConstants.AutomationBaseFormat, Uri.EscapeDataString(environmentName))}/companies";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.UseBearer(accessToken);

        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BcApiException(null, "Couldn't reach the Business Central automation API.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BC companies call for environment {Env} returned {Status}.", environmentName, response.StatusCode);
                throw new BcApiException(response.StatusCode,
                    $"The automation API returned {(int)response.StatusCode} when listing companies.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseCompanies(body);
        }
    }

    /// <summary>Parses the automation API <c>{ "value": [ { id, name, displayName } ] }</c> envelope. Internal for the client test.</summary>
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
}
