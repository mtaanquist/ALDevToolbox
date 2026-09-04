using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One GitHub App installation, as GitHub describes it. <see cref="Permissions"/>
/// maps a permission name to <c>read</c> or <c>write</c> — the Repositories tab
/// renders it so an admin can see a missing grant before someone hits it.
/// </summary>
public sealed record GitHubInstallation(
    long Id,
    string AccountLogin,
    string AccountType,
    IReadOnlyDictionary<string, string> Permissions)
{
    /// <summary>True when the installation sits on a GitHub organisation rather than a personal account.</summary>
    public bool IsOrganization => string.Equals(AccountType, "Organization", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The toolbox's client for the GitHub REST API, acting as the GitHub App.
///
/// <para>Hand-rolled on <see cref="HttpClient"/> rather than Octokit: the
/// milestone needs a dozen calls and an object model we would immediately wrap
/// is not worth a new dependency (see <c>.design/github-integration.md</c>).</para>
///
/// <para>Two credentials pass through here. The <em>App JWT</em>
/// (<see cref="GitHubAppJwt"/>) proves we are the App and is only good for the
/// <c>/app/*</c> routes. The <em>installation token</em> it mints acts as the
/// connected organisation and is what every later feature carries; it lasts an
/// hour and is cached per installation until five minutes before it lapses, so
/// a page that makes several calls does not mint several tokens.</para>
/// </summary>
public sealed class GitHubAppClient
{
    /// <summary>GitHub's REST base. Fixed public host, so no SSRF guard is needed.</summary>
    public const string ApiBaseUrl = "https://api.github.com/";

    /// <summary>
    /// Renew this far before the token actually expires, so a call that starts
    /// just under the wire doesn't finish with a dead token.
    /// </summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly SystemSettingsService _settings;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubAppClient> _logger;

    public GitHubAppClient(
        HttpClient http,
        SystemSettingsService settings,
        IMemoryCache cache,
        TimeProvider clock,
        ILogger<GitHubAppClient> logger)
    {
        _http = http;
        _settings = settings;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Reads one installation: which account it sits on and what it was granted.
    /// Uses the App JWT — an installation token cannot read this route.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No usable App registration on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused the call.</exception>
    public async Task<GitHubInstallation> GetInstallationAsync(long installationId, CancellationToken ct = default)
    {
        var jwt = await CreateAppJwtAsync(ct);
        using var request = NewRequest(HttpMethod.Get, $"app/installations/{installationId}", jwt);
        using var document = await SendAsync(request, ct);

        var root = document.RootElement;
        var account = root.TryGetProperty("account", out var acc) ? acc : default;
        var login = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("login", out var l)
            ? l.GetString() ?? string.Empty
            : string.Empty;
        var type = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("type", out var t)
            ? t.GetString() ?? string.Empty
            : root.TryGetProperty("target_type", out var tt) ? tt.GetString() ?? string.Empty : string.Empty;

        var installation = new GitHubInstallation(
            Id: root.TryGetProperty("id", out var id) && id.TryGetInt64(out var idValue) ? idValue : installationId,
            AccountLogin: login,
            AccountType: type,
            Permissions: ReadPermissions(root));

        _logger.LogInformation(
            "Read GitHub installation {InstallationId} on {AccountLogin} ({AccountType}) with {PermissionCount} permissions.",
            installation.Id, installation.AccountLogin, installation.AccountType, installation.Permissions.Count);
        return installation;
    }

    /// <summary>
    /// Returns an installation access token for <paramref name="installationId"/>,
    /// minting a fresh one only when the cached token is missing or within
    /// <see cref="RenewBefore"/> of expiry.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No usable App registration on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused to mint a token (revoked or suspended installation).</exception>
    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken ct = default)
    {
        var key = $"github:installation-token:{installationId}";
        if (_cache.TryGetValue(key, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var jwt = await CreateAppJwtAsync(ct);
        using var request = NewRequest(
            HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        using var document = await SendAsync(request, ct);

        var root = document.RootElement;
        var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return an access token.");
        }

        var expiresAt = root.TryGetProperty("expires_at", out var e)
            && e.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : _clock.GetUtcNow().AddHours(1);
        var ttl = expiresAt - _clock.GetUtcNow() - RenewBefore;
        if (ttl > TimeSpan.Zero)
        {
            _cache.Set(key, token, ttl);
        }

        _logger.LogInformation(
            "Minted a GitHub installation token for installation {InstallationId}, valid until {ExpiresAt:O}.",
            installationId, expiresAt);
        return token;
    }

    /// <summary>Reads the flat <c>permissions</c> object into a name -&gt; read|write map.</summary>
    private static IReadOnlyDictionary<string, string> ReadPermissions(JsonElement root)
    {
        var permissions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("permissions", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    permissions[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        return permissions;
    }

    /// <summary>
    /// Signs a fresh App JWT from the deployment's stored credentials. Cheap
    /// enough (one RSA signature) that caching it would buy nothing but a
    /// staleness bug.
    /// </summary>
    private async Task<string> CreateAppJwtAsync(CancellationToken ct)
    {
        var app = await _settings.ResolveGitHubAppAsync(ct)
            ?? throw new GitHubAppNotConfiguredException();
        return GitHubAppJwt.Create(app.AppId, app.PrivateKeyPem, _clock.GetUtcNow());
    }

    /// <summary>
    /// A request carrying <paramref name="credential"/> as a bearer token.
    /// GitHub accepts <c>Bearer</c> for both the App JWT and installation
    /// tokens, so there is only ever one scheme.
    /// </summary>
    private static HttpRequestMessage NewRequest(HttpMethod method, string path, string credential)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    /// <summary>
    /// Sends the request and returns the parsed body, translating any failure
    /// status into a <see cref="GitHubApiException"/> carrying GitHub's own
    /// <c>message</c>. Rate-limit headers are logged rather than surfaced —
    /// nothing in the UI can act on them.
    /// </summary>
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining))
        {
            _logger.LogDebug(
                "GitHub rate limit after {Method} {Path}: {Remaining} remaining.",
                request.Method, request.RequestUri, string.Join(',', remaining));
        }

        if (!response.IsSuccessStatusCode)
        {
            var (message, documentationUrl) = ReadError(body);
            _logger.LogWarning(
                "GitHub refused {Method} {Path} with {Status}: {Message}",
                request.Method, request.RequestUri, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, documentationUrl);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "GitHub returned a body that is not JSON for {Method} {Path}.",
                request.Method, request.RequestUri);
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub returned an unexpected response.");
        }
    }

    /// <summary>
    /// Pulls <c>message</c> / <c>documentation_url</c> out of a GitHub error
    /// body, falling back to a plain sentence when the body isn't the shape we
    /// expect (a proxy's HTML error page, say).
    /// </summary>
    private static (string Message, string? DocumentationUrl) ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var url = root.TryGetProperty("documentation_url", out var d) ? d.GetString() : null;
            return (string.IsNullOrWhiteSpace(message) ? "GitHub refused the request." : message!, url);
        }
        catch (JsonException)
        {
            return ("GitHub refused the request.", null);
        }
    }
}
