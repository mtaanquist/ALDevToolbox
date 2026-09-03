using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Antiforgery enforcement is a single-line opt-in
/// (<c>ValidateAntiforgeryAsync</c> at the top of every POST handler in
/// <c>Program.cs</c>). The framework owns the actual cookie/token shape;
/// these tests pin that we wire the validation up — a POST with no token
/// must reject before the handler runs.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class AntiforgeryRejectionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public AntiforgeryRejectionTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signup")]
    [InlineData("/auth/forgot-password")]
    [InlineData("/auth/logout")]
    public async Task Post_without_antiforgery_token_is_rejected(string path)
    {
        using var client = _factory.CreateClient();
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", "user@example.com"),
            new KeyValuePair<string, string>("Password", "verylongpassword12345"),
        });

        using var response = await client.PostAsync(path, body);

        // Antiforgery refusal can surface as 400 (validation throws) or as a
        // 200/redirect when ValidateAntiforgeryAsync silently swallows the
        // failure — pin the negative shape: never a successful action
        // outcome (200 with a Set-Cookie auth cookie).
        response.StatusCode.Should().NotBe(HttpStatusCode.OK,
            $"{path} must not run its handler without a valid antiforgery token");
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : Array.Empty<string>();
        setCookie.Should().NotContain(c => c.StartsWith("alwb_auth=", StringComparison.Ordinal),
            "an auth cookie shipped on an antiforgery failure means the handler ran");
    }

    /// <summary>
    /// The OAuth consent POST binds <c>HttpContext</c> and reads the form by
    /// hand, so <c>UseAntiforgery()</c> never covers it — the handler has to
    /// call the helper itself (issue #671). The request below is a *valid*
    /// authorisation request for a freshly registered client, so OpenIddict's
    /// middleware passes it through to our handler; only the antiforgery token
    /// is missing. Without the guard the handler runs and redirects to the
    /// login challenge, so the 400 (and its body) is what pins the fix.
    /// </summary>
    [Fact]
    public async Task OAuth_authorize_post_without_antiforgery_token_is_rejected()
    {
        using var client = _factory.CreateClient();

        // Anonymous Dynamic Client Registration — the same door the attack in
        // the issue walks through.
        using var registration = await client.PostAsync(
            "/oauth/register",
            new StringContent(
                """{"redirect_uris":["http://127.0.0.1:41999/callback"],"client_name":"CRONUS test client"}""",
                System.Text.Encoding.UTF8,
                "application/json"));
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registered = System.Text.Json.JsonDocument.Parse(
            await registration.Content.ReadAsStringAsync());
        var clientId = registered.RootElement.GetProperty("client_id").GetString()!;

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("decision", "allow"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("redirect_uri", "http://127.0.0.1:41999/callback"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("scope", "mcp"),
            new KeyValuePair<string, string>("code_challenge", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"),
            new KeyValuePair<string, string>("code_challenge_method", "S256"),
        });

        using var response = await client.PostAsync("/oauth/authorize", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the consent POST must refuse before running without a valid antiforgery token");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Antiforgery");
    }
}
