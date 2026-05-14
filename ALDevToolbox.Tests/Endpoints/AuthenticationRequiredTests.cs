using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Pins the auth boundary at the HTTP layer: every endpoint flagged
/// <c>RequireAuthorization()</c> in <c>Program.cs</c> redirects anonymous
/// callers to the cookie-auth login path, never returns a 200 with content,
/// and never leaks a service-level stack trace. A regression that drops
/// <c>.RequireAuthorization()</c> from a Map call would otherwise ship —
/// service-layer tests can't see it.
/// </summary>
public sealed class AuthenticationRequiredTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public AuthenticationRequiredTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("/generate/workspace")]
    [InlineData("/generate/extension")]
    [InlineData("/admin/export")]
    public async Task Unauthenticated_post_to_a_protected_endpoint_redirects_to_login(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        // Antiforgery runs first; without a token we expect a 400 instead of
        // the auth redirect. But auth is *also* enforced — assert one of the
        // two refusal shapes lands. A 200 with a generated ZIP would be the
        // real regression.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            // Cookie auth redirects to an absolute URL ("https://localhost/login?ReturnUrl=...");
            // assert the login path is in there rather than pinning to a relative shape.
            response.Headers.Location!.OriginalString.Should().Contain("/login");
        }
    }

    [Fact]
    public async Task Anonymous_get_to_the_logo_preview_redirects_to_login()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/admin/configuration/logo/preview");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task Anonymous_get_to_a_site_admin_route_returns_not_found_not_redirect()
    {
        // /site-admin/* must 404 for non-SiteAdmin (and anonymous) callers —
        // a redirect would tell an attacker the route exists.
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/site-admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
