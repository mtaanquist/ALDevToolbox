using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// One user-to-server token pair, as GitHub's OAuth token endpoint returns it.
///
/// <para><see cref="ExpiresAt"/> and <see cref="RefreshToken"/> are null when the
/// App has not opted in to expiring user tokens: in that case the access token
/// stands until the person revokes it on GitHub, and there is nothing to
/// refresh. Every other case gets an 8-hour access token and a 6-month refresh
/// token. See <c>.design/github-integration.md</c>.</para>
/// </summary>
public sealed record GitHubUserTokens(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt);

/// <summary>Who a user-to-server token belongs to: GitHub's stable numeric id and the renameable login.</summary>
public sealed record GitHubUserIdentity(long Id, string Login);

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
///
/// <para>From issue #621 a third credential passes through: a <em>user-to-server
/// token</em>, which acts as one person and is what every question of the form
/// "may this person see or do this?" is asked with. It is minted and refreshed
/// here and stored, encrypted, by <see cref="GitHubAccessService"/> — this class
/// never touches the database.</para>
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

    // ── User-to-server: acting as one person rather than as the organisation ──

    /// <summary>
    /// Trades the <c>code</c> GitHub sent back to <c>/signin-github</c> for a
    /// user-to-server token pair. No <c>redirect_uri</c> is sent: GitHub then
    /// uses the App's own registered callback URL, which is one fewer thing to
    /// keep in step across a reverse proxy.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No app id, private key, or OAuth client id/secret on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused the exchange (a stale or reused code).</exception>
    public Task<GitHubUserTokens> ExchangeUserCodeAsync(string code, CancellationToken ct = default) =>
        PostOAuthAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
        }, "authorization code exchange", ct);

    /// <summary>
    /// Mints a fresh access token from a stored refresh token. GitHub rotates
    /// the refresh token too, so the caller must persist the whole returned
    /// pair, not just the access half.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No app id, private key, or OAuth client id/secret on this deployment.</exception>
    /// <exception cref="GitHubApiException">The refresh token has expired or been revoked; the user has to link again.</exception>
    public Task<GitHubUserTokens> RefreshUserTokenAsync(string refreshToken, CancellationToken ct = default) =>
        PostOAuthAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "token refresh", ct);

    /// <summary>
    /// Who the token belongs to. The numeric id is what we store: a GitHub
    /// login can be renamed, and matching on it would silently follow whoever
    /// claimed the old name next.
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the call, e.g. a revoked token.</exception>
    public async Task<GitHubUserIdentity> GetAuthenticatedUserAsync(string userToken, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "user", userToken);
        using var document = await SendAsync(request, ct);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var idValue) ? idValue : 0;
        var login = root.TryGetProperty("login", out var loginElement) ? loginElement.GetString() ?? string.Empty : string.Empty;
        if (id <= 0 || login.Length == 0)
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say which account that was.");
        }
        return new GitHubUserIdentity(id, login);
    }

    /// <summary>
    /// Whether <paramref name="userToken"/>'s owner can see
    /// <c>{owner}/{repo}</c>.
    ///
    /// <para><strong>GitHub answers 404, not 403, for a repository you cannot
    /// see</strong> — telling you it exists would itself leak something. So a
    /// 404 here means "not visible to this person", never "gone", and the two
    /// are not distinguishable from outside. A 301 means the repository was
    /// renamed and is still visible.</para>
    /// </summary>
    public async Task<bool> UserCanSeeRepositoryAsync(
        string userToken, string owner, string repo, CancellationToken ct = default)
    {
        var status = await ProbeAsync(
            HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}",
            userToken, ct);
        return status is HttpStatusCode.OK or HttpStatusCode.MovedPermanently;
    }

    /// <summary>
    /// Whether <paramref name="username"/> is a member of <paramref name="org"/>,
    /// asked with that person's own token.
    ///
    /// <para>GitHub answers 204 for "yes", 404 for "no", and 302 when the
    /// asker is not in the organisation at all — which, since the asker here is
    /// the person being asked about, is another way of saying no. The client
    /// does not follow redirects (see <c>GitHubRegistration</c>) precisely so
    /// that 302 stays visible instead of turning into a confusing 403 from
    /// wherever it pointed.</para>
    /// </summary>
    public async Task<bool> UserIsOrgMemberAsync(
        string userToken, string org, string username, CancellationToken ct = default)
    {
        var status = await ProbeAsync(
            HttpMethod.Get,
            $"orgs/{Uri.EscapeDataString(org)}/members/{Uri.EscapeDataString(username)}",
            userToken, ct);
        return status == HttpStatusCode.NoContent;
    }

    /// <summary>
    /// The installations of this App that <paramref name="userToken"/>'s owner
    /// may administer. This is the only credential that can answer that — the
    /// App JWT can read <em>every</em> installation of the App, which is why
    /// the install callback cannot use it to decide whose organisation is
    /// being connected. See "Binding the installation to the acting user" in
    /// <c>.design/github-integration.md</c>.
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the call, e.g. a revoked token.</exception>
    public async Task<IReadOnlyList<long>> ListUserInstallationIdsAsync(
        string userToken, CancellationToken ct = default)
    {
        var ids = new List<long>();
        // GitHub pages this at 30 by default; 100 is the cap and nobody
        // administers more installations of one app than that in practice.
        var path = "user/installations?per_page=100";
        using var request = NewRequest(HttpMethod.Get, path, userToken);
        using var document = await SendAsync(request, ct);

        if (document.RootElement.TryGetProperty("installations", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray())
            {
                if (element.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
                {
                    ids.Add(value);
                }
            }
        }

        _logger.LogInformation("GitHub reported {InstallationCount} installations for the acting user.", ids.Count);
        return ids;
    }

    // ── Repositories: reading, and writing through a pull request ───────────

    /// <summary>
    /// Every repository the installation was granted, newest page first.
    /// Carries the <em>installation</em> token, so the answer is "what the
    /// organisation shared with the app", not "what one person may see" -
    /// narrowing to the latter is
    /// <see cref="GitHubAccessService.FilterAccessibleAsync"/>'s job, and the
    /// two together are what the repository picker offers.
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the call.</exception>
    public async Task<IReadOnlyList<GitHubRepositorySummary>> ListInstallationRepositoriesAsync(
        string installationToken, CancellationToken ct = default)
    {
        var repositories = new List<GitHubRepositorySummary>();
        // GitHub pages this at 30 by default and caps a page at 100. The loop
        // stops at ten pages: a thousand repositories is far past the point
        // where a typeahead is the right control, and an unbounded loop behind
        // a page render is how one bad answer becomes a hung request.
        for (var page = 1; page <= 10; page++)
        {
            using var request = NewRequest(
                HttpMethod.Get, $"installation/repositories?per_page=100&page={page}", installationToken);
            using var document = await SendAsync(request, ct);
            var root = document.RootElement;
            if (!root.TryGetProperty("repositories", out var list) || list.ValueKind != JsonValueKind.Array) break;

            var before = repositories.Count;
            foreach (var element in list.EnumerateArray())
            {
                if (ReadRepository(element) is { } repository) repositories.Add(repository);
            }
            if (repositories.Count - before < 100) break;
        }

        _logger.LogInformation(
            "GitHub reported {RepositoryCount} repositories for the installation.", repositories.Count);
        return repositories;
    }

    /// <summary>
    /// One repository as <paramref name="credential"/>'s owner sees it, or
    /// <see langword="null"/> when they cannot see it.
    ///
    /// <para>As in <see cref="UserCanSeeRepositoryAsync"/>, GitHub's 404 here
    /// means "not visible to you" and never "gone".</para>
    /// </summary>
    public async Task<GitHubRepositorySummary?> GetRepositoryAsync(
        string credential, string owner, string repo, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, RepoPath(owner, repo), credential);
        using var document = await SendOrNotFoundAsync(request, ct);
        return document is null ? null : ReadRepository(document.RootElement);
    }

    /// <summary>
    /// One file from a repository, decoded, or <see langword="null"/> when the
    /// repository has no such file. A missing file is an ordinary answer here -
    /// "this repository was not generated by the toolbox" - not a failure.
    ///
    /// <para>Returns null too when GitHub answers with a directory listing (an
    /// array) or with a file too large for the Contents API to inline, both of
    /// which mean the same thing to every caller: there is no file to read at
    /// that path.</para>
    /// </summary>
    public async Task<GitHubFileContent?> GetFileAsync(
        string credential, string owner, string repo, string path, string? gitRef = null,
        CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(gitRef) ? string.Empty : $"?ref={Uri.EscapeDataString(gitRef)}";
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/contents/{EscapePath(path)}{query}", credential);
        using var document = await SendOrNotFoundAsync(request, ct);
        if (document is null) return null;

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        var sha = root.TryGetProperty("sha", out var shaElement) ? shaElement.GetString() ?? string.Empty : string.Empty;

        try
        {
            // GitHub wraps base64 at 60 characters, so the newlines have to go
            // before Convert will look at it.
            var raw = Convert.FromBase64String((content.GetString() ?? string.Empty)
                .Replace("\n", string.Empty).Replace("\r", string.Empty));
            return new GitHubFileContent(path, Encoding.UTF8.GetString(raw), sha);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "GitHub returned a {Path} body that is not base64.", path);
            return null;
        }
    }

    /// <summary>
    /// The commit a branch points at, or <see langword="null"/> when there is
    /// no such branch. Used both to find what to branch from and to tell
    /// whether a branch name is already taken.
    /// </summary>
    public async Task<string?> GetBranchHeadShaAsync(
        string credential, string owner, string repo, string branch, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/git/ref/heads/{EscapePath(branch)}", credential);
        using var document = await SendOrNotFoundAsync(request, ct);
        if (document is null) return null;
        return document.RootElement.TryGetProperty("object", out var obj)
            && obj.TryGetProperty("sha", out var sha)
                ? sha.GetString()
                : null;
    }

    /// <summary>The tree a commit points at - the base a new tree is layered onto.</summary>
    public async Task<string> GetCommitTreeShaAsync(
        string credential, string owner, string repo, string commitSha, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/git/commits/{EscapePath(commitSha)}", credential);
        using var document = await SendAsync(request, ct);
        return ReadSha(document.RootElement, "tree")
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say what tree that commit points at.");
    }

    /// <summary>
    /// Uploads one file's bytes as a blob and returns its sha. Content goes up
    /// base64-encoded so a binary file survives the round trip unchanged.
    /// </summary>
    public async Task<string> CreateBlobAsync(
        string credential, string owner, string repo, byte[] content, CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/git/blobs", credential,
            new { content = Convert.ToBase64String(content), encoding = "base64" });
        using var document = await SendAsync(request, ct);
        return ReadOwnSha(document.RootElement)
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return a blob sha.");
    }

    /// <summary>
    /// Layers <paramref name="entries"/> onto <paramref name="baseTreeSha"/>
    /// and returns the resulting tree's sha. Every entry is a normal file
    /// (mode <c>100644</c>): the generator emits no symlinks or executables.
    /// </summary>
    public async Task<string> CreateTreeAsync(
        string credential, string owner, string repo, string baseTreeSha,
        IEnumerable<(string Path, string BlobSha)> entries, CancellationToken ct = default)
    {
        var tree = entries
            .Select(e => new { path = e.Path, mode = "100644", type = "blob", sha = e.BlobSha })
            .ToList();
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/git/trees", credential,
            new { base_tree = baseTreeSha, tree });
        using var document = await SendAsync(request, ct);
        return ReadOwnSha(document.RootElement)
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return a tree sha.");
    }

    /// <summary>Commits <paramref name="treeSha"/> on top of <paramref name="parentSha"/>.</summary>
    public async Task<string> CreateCommitAsync(
        string credential, string owner, string repo, string message, string treeSha, string parentSha,
        CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/git/commits", credential,
            new { message, tree = treeSha, parents = new[] { parentSha } });
        using var document = await SendAsync(request, ct);
        return ReadOwnSha(document.RootElement)
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return a commit sha.");
    }

    /// <summary>
    /// Points a new branch at <paramref name="commitSha"/>. Returns
    /// <see langword="false"/> when the name is already taken, which the caller
    /// answers by trying the next one rather than by moving somebody else's
    /// branch.
    /// </summary>
    public async Task<bool> CreateBranchAsync(
        string credential, string owner, string repo, string branch, string commitSha,
        CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/git/refs", credential,
            new { @ref = $"refs/heads/{branch}", sha = commitSha });
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return true;

        var (message, url) = ReadError(body);
        // GitHub answers 422 both for "Reference already exists" - a name the
        // caller can step past - and for a request that was wrong (a bad sha,
        // an illegal ref name). Only the first is an answer; treating the second
        // as one would retry a broken request nine more times and then blame the
        // user's branches for it.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity
            && message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _logger.LogWarning(
            "GitHub refused to create branch {Branch} on {Owner}/{Repo} with {Status}: {Message}",
            branch, owner, repo, (int)response.StatusCode, message);
        throw new GitHubApiException(response.StatusCode, message, url);
    }

    /// <summary>
    /// Opens a pull request from <paramref name="head"/> into
    /// <paramref name="baseBranch"/>, as <paramref name="credential"/>'s owner.
    /// </summary>
    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        string credential, string owner, string repo,
        string title, string head, string baseBranch, string body, CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/pulls", credential,
            new { title, head, @base = baseBranch, body });
        using var document = await SendAsync(request, ct);
        var root = document.RootElement;
        var number = root.TryGetProperty("number", out var n) && n.TryGetInt32(out var value) ? value : 0;
        var htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (number <= 0 || string.IsNullOrEmpty(htmlUrl))
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say which pull request it opened.");
        }

        _logger.LogInformation(
            "Opened pull request #{PullRequestNumber} on {Owner}/{Repo} from {Head} into {Base}.",
            number, owner, repo, head, baseBranch);
        return new GitHubPullRequest(number, htmlUrl!, head);
    }

    /// <summary>Reads GitHub's repository object into the one shape the toolbox carries.</summary>
    private static GitHubRepositorySummary? ReadRepository(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var fullName = element.TryGetProperty("full_name", out var f) ? f.GetString() : null;
        if (string.IsNullOrWhiteSpace(fullName)) return null;

        var parts = fullName!.Split('/', 2);
        if (parts.Length != 2) return null;

        return new GitHubRepositorySummary(
            FullName: fullName,
            Owner: parts[0],
            Name: parts[1],
            // A repository with no commits yet has no default branch to speak
            // of; "main" is what GitHub would create, and every caller that
            // needs a real branch reads its head before using it.
            DefaultBranch: element.TryGetProperty("default_branch", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "main"
                : "main",
            IsPrivate: element.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            Description: element.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null,
            HtmlUrl: element.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty,
            CloneUrl: element.TryGetProperty("clone_url", out var c) ? c.GetString() ?? string.Empty : string.Empty);
    }

    private static string RepoPath(string owner, string repo) =>
        $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    /// <summary>
    /// Escapes a path segment by segment: the slashes in
    /// <c>Translations/en-GB.xlf</c> are part of the route, and escaping them
    /// would ask GitHub for a file whose name contains them.
    /// </summary>
    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string? ReadOwnSha(JsonElement root) =>
        root.TryGetProperty("sha", out var sha) ? sha.GetString() : null;

    private static string? ReadSha(JsonElement root, string property) =>
        root.TryGetProperty(property, out var child)
            && child.ValueKind == JsonValueKind.Object
            && child.TryGetProperty("sha", out var sha)
                ? sha.GetString()
                : null;

    /// <summary>A request carrying <paramref name="body"/> as JSON.</summary>
    private static HttpRequestMessage NewJsonRequest(
        HttpMethod method, string path, string credential, object body)
    {
        var request = NewRequest(method, path, credential);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// <see cref="SendAsync"/> for the reads where GitHub's 404 is an answer
    /// rather than a failure: a repository you cannot see, a file that is not
    /// there, a branch that does not exist. Every other failure status still
    /// throws.
    /// </summary>
    private async Task<JsonDocument?> SendOrNotFoundAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("GitHub has nothing at {Method} {Path}.", request.Method, request.RequestUri);
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var (message, documentationUrl) = ReadError(body);
            _logger.LogWarning(
                "GitHub refused {Method} {Path} with {Status}: {Message}",
                request.Method, request.RequestUri, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, documentationUrl);
        }
        return ParseOrThrow(body, request.RequestUri?.ToString());
    }

    /// <summary>
    /// Posts to GitHub's OAuth token endpoint, which lives on <c>github.com</c>
    /// rather than the API host and answers <c>200 OK</c> even for failures,
    /// with an <c>error</c> field instead of an error status. Both cases are
    /// normalised into <see cref="GitHubApiException"/> here so callers see one
    /// failure shape.
    /// </summary>
    private async Task<GitHubUserTokens> PostOAuthAsync(
        IDictionary<string, string> form, string what, CancellationToken ct)
    {
        var app = await _settings.ResolveGitHubAppAsync(ct) ?? throw new GitHubAppNotConfiguredException();
        if (string.IsNullOrEmpty(app.ClientId) || string.IsNullOrEmpty(app.ClientSecret))
        {
            throw new GitHubAppNotConfiguredException();
        }

        form["client_id"] = app.ClientId;
        form["client_secret"] = app.ClientSecret;

        using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        // The endpoint defaults to a form-encoded body; ask for JSON, and don't
        // let the API host's vendor Accept header ride along.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (message, url) = ReadError(body);
            _logger.LogWarning("GitHub refused a user {What} with {Status}: {Message}", what, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, url);
        }

        using var document = ParseOrThrow(body, OAuthTokenUrl);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var message = string.IsNullOrWhiteSpace(description) ? error.GetString()! : description!;
            _logger.LogWarning("GitHub refused a user {What}: {Message}", what, message);
            throw new GitHubApiException(HttpStatusCode.BadRequest, message);
        }

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not return an access token.");
        }

        var now = _clock.GetUtcNow();
        _logger.LogInformation("Completed a GitHub user {What}.", what);
        return new GitHubUserTokens(
            AccessToken: accessToken,
            ExpiresAt: ReadLifetime(root, "expires_in") is { } ttl ? now.Add(ttl) : null,
            RefreshToken: root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            RefreshTokenExpiresAt: ReadLifetime(root, "refresh_token_expires_in") is { } refreshTtl
                ? now.Add(refreshTtl)
                : null);
    }

    /// <summary>
    /// Reads one of GitHub's <c>*_in</c> second counts. Absent means the App
    /// does not expire user tokens, which is a supported configuration, not an
    /// error — the caller stores a null expiry and never refreshes.
    /// </summary>
    private static TimeSpan? ReadLifetime(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// Sends a request whose <em>status</em> is the answer, and returns it
    /// rather than throwing. Used for the yes/no probes where GitHub's
    /// not-found is a legitimate "no" rather than a failure.
    /// </summary>
    private async Task<HttpStatusCode> ProbeAsync(
        HttpMethod method, string path, string credential, CancellationToken ct)
    {
        using var request = NewRequest(method, path, credential);
        using var response = await _http.SendAsync(request, ct);
        _logger.LogDebug("GitHub answered {Status} for {Method} {Path}.", (int)response.StatusCode, method, path);
        return response.StatusCode;
    }

    /// <summary>GitHub's OAuth token endpoint. Not on the API host, so it is addressed absolutely.</summary>
    private const string OAuthTokenUrl = "https://github.com/login/oauth/access_token";

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

        return ParseOrThrow(body, request.RequestUri?.ToString());
    }

    /// <summary>
    /// Parses a body we have already decided is a success, turning "GitHub
    /// answered with something that isn't JSON" (a proxy's HTML page, most
    /// often) into the same failure shape as a refusal.
    /// </summary>
    private JsonDocument ParseOrThrow(string body, string? path)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "GitHub returned a body that is not JSON for {Path}.", path);
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
