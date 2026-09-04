using System.Net;
using System.Text;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// A stand-in for GitHub's HTTP surface.
///
/// <para><c>api.github.com</c> is not reachable from the build environment, and
/// would be the wrong thing to depend on if it were: these tests are about how
/// the toolbox reacts to GitHub's documented answers - a 404 for a repository
/// you cannot see, a 302 for an organisation you are not in, a rotated refresh
/// token - not about GitHub itself. Each route is registered by the method and
/// path prefix the production code calls, so a change to either shows up as a
/// missed route rather than a silently different test.</para>
/// </summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = new();

    /// <summary>Every request the handler saw, as "METHOD absolute-uri", in order.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Bodies of the form posts to the OAuth token endpoint, in order.</summary>
    public List<string> OAuthBodies { get; } = new();

    /// <summary>
    /// Every request body the handler saw, keyed by "METHOD absolute-uri" - so
    /// a test can assert what went <em>into</em> a commit, not only that one was
    /// attempted.
    /// </summary>
    public List<(string Call, string Body)> Bodies { get; } = new();

    /// <summary>
    /// Registers a reply for requests whose path starts with
    /// <paramref name="path"/>. When several routes match, the longest wins, so
    /// <c>/user/installations</c> is not swallowed by <c>/user</c>; between two
    /// routes of the same length the later registration wins, so a helper's
    /// default answer can be overridden by the test that needs a different one.
    /// </summary>
    public FakeGitHubApi On(HttpMethod method, string path, HttpStatusCode status, string? json = null)
    {
        _routes.Add((method.Method, Normalise(path), _ => Respond(status, json)));
        return this;
    }

    /// <summary>
    /// Registers a reply that changes with each call - the sequence is consumed
    /// in order and the last entry repeats, which is how a refresh test says
    /// "expired, then good".
    /// </summary>
    public FakeGitHubApi OnSequence(HttpMethod method, string path, params (HttpStatusCode Status, string? Json)[] replies)
    {
        var index = 0;
        _routes.Add((method.Method, Normalise(path), _ =>
        {
            var reply = replies[Math.Min(index, replies.Length - 1)];
            index++;
            return Respond(reply.Status, reply.Json);
        }));
        return this;
    }

    /// <summary>A user-to-server token response, in the shape GitHub's OAuth endpoint returns.</summary>
    public static string TokenJson(
        string accessToken = "ghu_access",
        int? expiresIn = 28800,
        string? refreshToken = "ghr_refresh",
        int? refreshExpiresIn = 15811200)
    {
        var parts = new List<string> { $"\"access_token\":\"{accessToken}\"", "\"token_type\":\"bearer\"" };
        if (expiresIn is not null) parts.Add($"\"expires_in\":{expiresIn}");
        if (refreshToken is not null) parts.Add($"\"refresh_token\":\"{refreshToken}\"");
        if (refreshExpiresIn is not null) parts.Add($"\"refresh_token_expires_in\":{refreshExpiresIn}");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>The <c>GET /user</c> body.</summary>
    public static string UserJson(long id = 4711, string login = "cronus-dev") =>
        $"{{\"id\":{id},\"login\":\"{login}\"}}";

    /// <summary>One repository, in the shape every repository route returns.</summary>
    public static string RepositoryJson(
        string fullName,
        string defaultBranch = "main",
        bool isPrivate = true,
        string? description = null)
    {
        var name = fullName.Split('/').Last();
        var describe = description is null ? "null" : $"\"{description}\"";
        return $$"""
            {"full_name":"{{fullName}}","name":"{{name}}","default_branch":"{{defaultBranch}}",
             "private":{{(isPrivate ? "true" : "false")}},"description":{{describe}},
             "html_url":"https://github.com/{{fullName}}","clone_url":"https://github.com/{{fullName}}.git"}
            """;
    }

    /// <summary>The <c>GET /installation/repositories</c> body.</summary>
    public static string InstallationRepositoriesJson(params string[] fullNames) =>
        $"{{\"total_count\":{fullNames.Length},\"repositories\":[{string.Join(',', fullNames.Select(f => RepositoryJson(f)))}]}}";

    /// <summary>The <c>POST /app/installations/{id}/access_tokens</c> body.</summary>
    public static string InstallationTokenJson(string token = "ghs_installation") =>
        $"{{\"token\":\"{token}\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddHours(1):O}\"}}";

    /// <summary>The Contents API's body for one file.</summary>
    public static string FileContentsJson(string path, string text, string sha = "blob-sha")
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return $"{{\"path\":\"{path}\",\"sha\":\"{sha}\",\"encoding\":\"base64\",\"content\":\"{encoded}\"}}";
    }

    /// <summary>A <c>{"sha": …}</c> body, which most Git Data writes answer with.</summary>
    public static string ShaJson(string sha) => $"{{\"sha\":\"{sha}\"}}";

    /// <summary>The <c>GET /user/installations</c> body.</summary>
    public static string InstallationsJson(params long[] ids) =>
        $"{{\"total_count\":{ids.Length},\"installations\":[{string.Join(',', ids.Select(i => $"{{\"id\":{i}}}"))}]}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        Calls.Add($"{request.Method.Method} {uri}");
        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Bodies.Add(($"{request.Method.Method} {uri}", body));
            if (uri.Contains("login/oauth/access_token")) OAuthBodies.Add(body);
        }

        var path = request.RequestUri.AbsolutePath;
        var match = _routes
            .Select((Route, Index) => (Route, Index))
            .Where(r => r.Route.Method == request.Method.Method
                && path.StartsWith(r.Route.Path, StringComparison.Ordinal))
            .OrderByDescending(r => r.Route.Path.Length)
            .ThenByDescending(r => r.Index)
            .Select(r => (r.Route, Found: true))
            .FirstOrDefault();
        if (match.Found) return Task.FromResult(match.Route.Reply(request));

        // An unregistered route is a test that asked GitHub something it did not
        // mean to; say which call, rather than failing later on an empty body.
        return Task.FromResult(Respond(HttpStatusCode.NotImplemented,
            $"{{\"message\":\"FakeGitHubApi has no route for {request.Method} {uri}\"}}"));
    }

    /// <summary>Routes are written without a leading slash for readability; the paths they match have one.</summary>
    private static string Normalise(string path) => path.StartsWith('/') ? path : "/" + path;

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json) =>
        new(status)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        };
}
