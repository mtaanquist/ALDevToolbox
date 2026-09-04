using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Startup;

/// <summary>
/// The GitHub App integration: the per-organisation connection and the REST
/// client that acts as the App. See <c>.design/github-integration.md</c>.
/// </summary>
public static class GitHubRegistration
{
    /// <summary>Registers the GitHub services and the typed API client.</summary>
    public static IServiceCollection AddGitHub(this IServiceCollection services)
    {
        services.AddScoped<GitHubConnectionService>();
        // Typed client on a fixed public host (api.github.com), so no SSRF
        // guard is needed - just a bounded timeout and the headers GitHub
        // requires on every request. Authorization is set per request, because
        // the same client carries both the App JWT and installation tokens.
        services.AddHttpClient<GitHubAppClient>(client =>
        {
            client.BaseAddress = new Uri(GitHubAppClient.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // GitHub rejects requests without a User-Agent, and pins API
            // behaviour to a dated version rather than "whatever shipped today".
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        return services;
    }
}
