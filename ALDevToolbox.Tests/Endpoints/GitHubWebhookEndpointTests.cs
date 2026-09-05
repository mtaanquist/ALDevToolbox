using System.Net;
using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The toolbox's one inbound route (issue #627), booted end to end so the real
/// pipeline is in the picture: anonymous, antiforgery-disabled, and refusing
/// everything whose HMAC does not match the deployment's stored webhook secret.
///
/// <para>The status codes matter as much as the behaviour. GitHub shows the
/// operator what came back per delivery, and
/// <c>UseStatusCodePagesWithReExecute</c> rewrites a bare 4xx on a POST into a
/// 400 - which would turn "your signature is wrong" into "your request is
/// malformed". So each refusal is asserted for its own status, not merely for
/// not-success.</para>
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GitHubWebhookEndpointTests : IDisposable
{
    private const string Secret = "swordfish";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public GitHubWebhookEndpointTests()
    {
        // The real worker would drain the queue as fast as the endpoint fills it,
        // so "was this delivery queued?" would be a race. These tests are about the
        // endpoint - what it accepts, what it refuses, and what it hands on - so
        // the drain is taken out and the channel left to be read by the test.
        _factory = new EndpointFactory(_db, services =>
        {
            var worker = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(GitHubPullRequestBuildWorker));
            if (worker is not null) services.Remove(worker);
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    private static string PullRequestPayload(
        string action = "opened",
        long installationId = 42,
        int number = 7,
        string headSha = "abc123") => $$"""
        {
          "action": "{{action}}",
          "installation": { "id": {{installationId}} },
          "repository": {
            "full_name": "cronus-dk/customer-app",
            "clone_url": "https://github.com/cronus-dk/customer-app.git"
          },
          "pull_request": {
            "number": {{number}},
            "head": { "sha": "{{headSha}}", "ref": "feature/vat" },
            "base": { "ref": "main" }
          }
        }
        """;

    private async Task StoreSecretAsync(string secret = Secret)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        await settings.SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-dev-toolbox", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: null, ClearPrivateKey: false,
            WebhookSecret: secret, ClearWebhookSecret: false));
    }

    private static HttpRequestMessage Delivery(string json, string? secret, string eventName = "pull_request")
    {
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubWebhookEndpoints.WebhookPath)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", "11111111-2222-3333-4444-555555555555");
        if (secret is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", Signature(secret, body));
        }
        return request;
    }

    /// <summary>
    /// GitHub's own header, computed here rather than through the production
    /// helper - a test that signs with the code under test would pass whatever
    /// that code did.
    /// </summary>
    private static string Signature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public async Task A_delivery_with_a_valid_signature_is_accepted_and_queued()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty(
            "every response writes a body so the status-pages middleware does not rewrite it");

        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.InstallationId.Should().Be(42);
        job.RepositoryFullName.Should().Be("cronus-dk/customer-app");
        job.PullRequestNumber.Should().Be(7);
        job.HeadSha.Should().Be("abc123");
        job.HeadRef.Should().Be("feature/vat");
        job.BaseRef.Should().Be("main");
        job.DeliveryId.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task A_delivery_signed_with_the_wrong_secret_is_refused_as_401()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), "not-the-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "GitHub's delivery log has to show what was wrong, not a generic 400");

        var queue = _factory.Services.GetRequiredService<GitHubWebhookQueue>();
        queue.Reader.TryRead(out _).Should().BeFalse("an unverified delivery must never reach the queue");
    }

    [Fact]
    public async Task A_delivery_with_no_signature_at_all_is_refused_as_401()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), secret: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_stored_secret_every_delivery_is_refused()
    {
        // Nothing is configured, so nothing can be verified - and an unverifiable
        // delivery is refused rather than trusted.
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_ping_is_answered_so_the_operator_knows_the_address_and_secret_are_right()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("{\"zen\":\"Keep it logically awesome.\"}", Secret, eventName: "ping"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("pong");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("labeled")]
    [InlineData("assigned")]
    public async Task A_pull_request_action_that_is_not_a_new_head_is_ignored(string action)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(action: action), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("synchronize")]
    [InlineData("reopened")]
    public async Task The_three_actions_that_mean_a_new_head_are_queued(string action)
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery(PullRequestPayload(action: action), Secret));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeTrue();
    }

    [Fact]
    public async Task An_event_we_do_not_act_on_is_answered_without_content()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("{}", Secret, eventName: "push"));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_pull_request_payload_missing_the_installation_is_not_queued()
    {
        // Without an installation there is no organisation to act for, so there
        // is nothing to do - and nothing to retry, hence not an error status.
        await StoreSecretAsync();
        using var client = _factory.CreateClient();
        const string NoInstallation = """
            {"action":"opened","repository":{"full_name":"a/b","clone_url":"https://github.com/a/b.git"},
             "pull_request":{"number":1,"head":{"sha":"abc","ref":"x"},"base":{"ref":"main"}}}
            """;

        using var response = await client.SendAsync(Delivery(NoInstallation, Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_not_queued()
    {
        await StoreSecretAsync();
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(Delivery("this is not json", Secret));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<GitHubWebhookQueue>()
            .Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void The_route_declares_a_body_cap()
    {
        // The in-memory test server does not implement the body-size feature, so
        // the cap cannot be exercised by sending a large body here - what can be
        // pinned is that the route asks for one, which is what makes an oversized
        // delivery a socket-level drop rather than a megabyte in memory.
        var endpoints = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;
        var webhook = endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == GitHubWebhookEndpoints.WebhookPath);

        var limit = webhook.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
        limit.Should().NotBeNull();
        limit!.MaxRequestBodySize.Should().Be(GitHubWebhookEndpoints.MaxRequestBodyBytes);
    }

    [Fact]
    public void The_signature_check_rejects_anything_that_is_not_a_sha256_hex_digest()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        GitHubWebhookEndpoints.SignatureMatches(Secret, body, null).Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, string.Empty).Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, "sha1=deadbeef").Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, "sha256=not-hex").Should().BeFalse();
        GitHubWebhookEndpoints.SignatureMatches(Secret, body, Signature(Secret, body)).Should().BeTrue();
        GitHubWebhookEndpoints.SignatureMatches("other", body, Signature(Secret, body)).Should().BeFalse();
    }
}
