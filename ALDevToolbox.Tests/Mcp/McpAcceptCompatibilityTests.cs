using System.Net;
using System.Text;
using ALDevToolbox.Endpoints;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// Pins <see cref="McpAcceptCompatibility"/>, the shim that lets MCP clients
/// behind an API gateway talk to <c>/mcp</c>.
///
/// <para>
/// The MCP SDK refuses a POST whose Accept does not name both
/// <c>application/json</c> and <c>text/event-stream</c>, and answers a
/// conforming POST with an SSE body regardless. Azure APIM (in front of
/// Copilot Studio) narrows Accept to <c>application/json</c>, so such a client
/// could neither pass the check nor read the reply. The shim widens the
/// request header and unwraps the reply.
/// </para>
///
/// <para>
/// These drive the middleware through a real pipeline rather than reaching
/// into its helpers: what matters is the bytes on either side of it, and the
/// promise that a conforming client's bytes are not touched. The terminal
/// handler stands in for the SDK, which keeps the test off the database and
/// the auth stack.
/// </para>
/// </summary>
public sealed class McpAcceptCompatibilityTests
{
    private const string SseResponse =
        "event: message\ndata: {\"result\":{\"tools\":[]},\"id\":7,\"jsonrpc\":\"2.0\"}\n\n";

    /// <summary>
    /// Spins up a pipeline of shim + fake MCP handler. The handler echoes the
    /// Accept header it observed into <c>X-Observed-Accept</c> so a test can
    /// assert on what the SDK would have seen.
    /// </summary>
    private static async Task<IHost> StartAsync(
        string responseContentType = "text/event-stream",
        string responseBody = SseResponse,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddLogging())
                .Configure(app =>
                {
                    app.UseMcpAcceptCompatibility();
                    app.Run(async ctx =>
                    {
                        ctx.Response.Headers["X-Observed-Accept"] =
                            (string?)ctx.Request.Headers.Accept ?? string.Empty;
                        ctx.Response.StatusCode = (int)status;
                        ctx.Response.ContentType = responseContentType;
                        await ctx.Response.WriteAsync(responseBody);
                    });
                }))
            .StartAsync();
    }

    private static HttpRequestMessage Post(string path, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}",
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", accept);
        return req;
    }

    [Fact]
    public async Task A_json_only_client_gets_the_sse_reply_unwrapped_to_json()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "a gateway client that only accepts JSON cannot read text/event-stream");
        (await res.Content.ReadAsStringAsync()).Should()
            .Be("{\"result\":{\"tools\":[]},\"id\":7,\"jsonrpc\":\"2.0\"}");
    }

    [Fact]
    public async Task The_request_reaching_the_sdk_names_both_media_types()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "application/json"));

        res.Headers.GetValues("X-Observed-Accept").Single().Should()
            .Be("application/json, text/event-stream",
                "the SDK refuses the request outright unless both are named");
    }

    [Fact]
    public async Task A_conforming_client_is_passed_through_untouched()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient()
            .SendAsync(Post("/mcp", "application/json, text/event-stream"));

        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        (await res.Content.ReadAsStringAsync()).Should().Be(SseResponse,
            "a client that asked for SSE must get the bytes the SDK produced");
        // Asserted as "both are named" rather than an exact string: HttpClient
        // normalises the header it sends, so pinning the spacing would test
        // HttpClient rather than the shim's promise to leave it alone.
        var observed = res.Headers.GetValues("X-Observed-Accept").Single();
        observed.Should().Contain("application/json").And.Contain("text/event-stream");
    }

    [Fact]
    public async Task A_wildcard_accept_is_left_alone()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "*/*"));

        // Measured against the real SDK: */* satisfies its check on its own,
        // so there is nothing to rescue and rewriting would only surprise.
        res.Headers.GetValues("X-Observed-Accept").Single().Should().Be("*/*");
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task A_non_sse_reply_keeps_its_status_and_body()
    {
        using var host = await StartAsync(
            responseContentType: "text/plain; charset=utf-8",
            responseBody: "MCP is disabled on this deployment.",
            status: HttpStatusCode.NotFound);

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the shim must not turn an auth or kill-switch refusal into a 200");
        (await res.Content.ReadAsStringAsync()).Should().Be("MCP is disabled on this deployment.");
    }

    [Fact]
    public async Task A_notification_ahead_of_the_response_is_dropped()
    {
        // JSON-RPC 2.0 §4.1: a notification carries no id, a response always
        // does. A JSON-only client wants the response, not the progress chatter.
        using var host = await StartAsync(responseBody:
            "event: message\ndata: {\"method\":\"notifications/progress\",\"jsonrpc\":\"2.0\"}\n\n"
            + "event: message\ndata: {\"result\":{\"tools\":[]},\"id\":7,\"jsonrpc\":\"2.0\"}\n\n");

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "application/json"));

        (await res.Content.ReadAsStringAsync()).Should()
            .Be("{\"result\":{\"tools\":[]},\"id\":7,\"jsonrpc\":\"2.0\"}");
    }

    [Fact]
    public async Task An_sse_body_with_no_response_message_is_passed_through_rather_than_guessed_at()
    {
        var notificationsOnly =
            "event: message\ndata: {\"method\":\"notifications/progress\",\"jsonrpc\":\"2.0\"}\n\n";
        using var host = await StartAsync(responseBody: notificationsOnly);

        var res = await host.GetTestClient().SendAsync(Post("/mcp", "application/json"));

        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream",
            "inventing a reply would be worse than handing back what the SDK actually said");
        (await res.Content.ReadAsStringAsync()).Should().Be(notificationsOnly);
    }

    [Fact]
    public async Task Paths_outside_mcp_are_not_touched()
    {
        using var host = await StartAsync();

        var res = await host.GetTestClient().SendAsync(Post("/generate/workspace", "application/json"));

        res.Headers.GetValues("X-Observed-Accept").Single().Should().Be("application/json",
            "the shim is scoped to /mcp — nothing else should have its Accept rewritten");
    }

    [Fact]
    public async Task The_get_sse_channel_is_not_buffered()
    {
        using var host = await StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await host.GetTestClient().SendAsync(req);

        res.Headers.GetValues("X-Observed-Accept").Single().Should().Be("application/json",
            "GET is the long-lived stream; buffering it would break streaming outright");
    }
}
