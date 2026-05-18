using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

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
}
