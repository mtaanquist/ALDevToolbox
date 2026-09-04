using System.Net;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// What a <c>404</c> from the Admin Center means, call by call. The reads and writes on
/// <see cref="BcAdminClient"/> share one send path, and the only thing each of them says
/// about a 404 is whether "Business Central doesn't have that" is an answer or a fault.
/// That per-call decision is pinned here so it stays a decision rather than whatever the
/// last edited copy happened to do (issue #695). See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcAdminClientNotFoundTests
{
    private const string Token = "tok";
    private const string Family = "BusinessCentral";
    private const string Environment = "Production";

    /// <summary>Answers every request with one canned status and body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    /// <summary>Stands in for a network that never answered.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static BcAdminClient Client(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") =>
        new(new StubFactory(new StubHandler(status, body)), NullLogger<BcAdminClient>.Instance);

    private static BcAdminClient UnreachableClient() =>
        new(new StubFactory(new UnreachableHandler()), NullLogger<BcAdminClient>.Instance);

    // ── 404 as an answer ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_hard_deleted_environment_reads_as_absent()
    {
        var client = Client(HttpStatusCode.NotFound, """{"code":"environmentNotFound","message":"x"}""");

        var environment = await client.GetEnvironmentAsync(Token, Family, Environment);

        environment.Should().BeNull("a delivery turns 'no longer there' into its own message");
    }

    [Fact]
    public async Task A_hard_deleted_environment_has_no_updates_on_offer()
    {
        var client = Client(HttpStatusCode.NotFound, """{"code":"environmentNotFound","message":"x"}""");

        var updates = await client.ListEnvironmentUpdatesAsync(Token, Family, Environment);

        updates.Should().BeEmpty("an environment that is gone has nothing scheduled, which the panel and the mirror both read as empty");
    }

    [Fact]
    public async Task A_hard_deleted_environment_has_no_update_window()
    {
        var client = Client(HttpStatusCode.NotFound, """{"code":"environmentNotFound","message":"x"}""");

        var settings = await client.GetUpdateSettingsAsync(Token, Family, Environment);

        settings.Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_404_as_absent_does_not_swallow_the_other_refusals()
    {
        var client = Client(HttpStatusCode.Forbidden, """{"code":"Forbidden","message":"No GDAP relationship."}""");

        var act = () => client.GetEnvironmentAsync(Token, Family, Environment);

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Message.Should().Contain("403").And.Contain("No GDAP relationship.");
    }

    // ── 404 as a fault ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_404_on_the_tenant_wide_list_is_a_fault_not_an_empty_fleet()
    {
        var client = Client(HttpStatusCode.NotFound, """{"code":"ResourceDoesNotExist","message":"x"}""");

        var act = () => client.ListEnvironmentsAsync(Token);

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        thrown.Message.Should().Contain("404",
            "an empty list here would show a broken connection as a customer with no environments");
    }

    [Fact]
    public async Task A_404_on_the_update_window_write_is_a_refusal()
    {
        var client = Client(HttpStatusCode.NotFound, """{"code":"environmentNotFound","message":"x"}""");

        var act = () => client.SetUpdateSettingsAsync(
            Token, Family, Environment, new TimeOnly(2, 0), new TimeOnly(6, 0), "Romance Standard Time");

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        thrown.Message.Should().Contain("no longer has this environment",
            "a write that silently did nothing would leave the consultant thinking the window was set");
    }

    // ── The transport fault, which is never a status ──────────────────────────

    [Fact]
    public async Task An_unreachable_api_says_what_it_was_doing()
    {
        var act = () => UnreachableClient().ListEnvironmentUpdatesAsync(Token, Family, Environment);

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().BeNull("nothing answered, so there is no status to report");
        thrown.Message.Should().Contain("reading the Business Central updates");
    }
}
