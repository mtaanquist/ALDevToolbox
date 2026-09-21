using System.Net;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Reading who is signed in to a customer's environment, and ending one of those
/// sessions. Two things are pinned here: the shape of what goes over the wire (a wrong
/// route on a DELETE is not something a read-back would catch, and this DELETE signs
/// somebody out), and that Microsoft's documented payload parses into something a
/// consultant can read.
/// <para>
/// The payload comes from <c>administration-center-api_session_management</c>. That page
/// prints its example with the field <em>types</em> in place of values, so the field names
/// and their order here are Microsoft's exactly and the values are plausible ones.
/// </para>
/// See <c>.design/environment-updates.md</c>, "Sessions".
/// </summary>
public sealed class BcSessionsClientTests
{
    private const string Token = "tok";
    private const string Family = "BusinessCentral";
    private const string Environment = "Production";

    /// <summary>
    /// Microsoft's documented session object, every field in the documented order, with
    /// values of the documented types - note <c>sessionId</c> and <c>currentObjectId</c>
    /// are integers while <c>entryPointObjectId</c> is a string, which is the API's own
    /// inconsistency and not a transcription slip.
    /// </summary>
    private const string DocumentedPayload = """
    {
      "value": [
        {
          "environmentName": "Production",
          "applicationFamily": "BusinessCentral",
          "sessionId": 47,
          "userId": "ola@cronus.example",
          "clientType": "WebClient",
          "logOnDate": "2026-09-20T07:12:44Z",
          "entryPointOperation": "OnRun",
          "entryPointObjectName": "Sales Order",
          "entryPointObjectId": "42",
          "entryPointObjectType": "Page",
          "currentObjectName": "Post Sales Documents",
          "currentObjectId": 82,
          "currentObjectType": "CodeUnit",
          "currentOperationDuration": 3720000
        }
      ]
    }
    """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
        { _status = status; _body = body; }

        public HttpMethod? Method { get; private set; }
        public Uri? Url { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Url = request.RequestUri;
            if (request.Content is not null) Body = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (BcAdminClient Client, RecordingHandler Handler) Client(
        HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var handler = new RecordingHandler(status, body);
        return (new BcAdminClient(new StubFactory(handler), NullLogger<BcAdminClient>.Instance), handler);
    }

    // ── The parser ────────────────────────────────────────────────────────

    [Fact]
    public void Microsofts_documented_session_parses_into_every_field()
    {
        var session = BcAdminClient.ParseSessions(DocumentedPayload).Should().ContainSingle().Which;

        session.SessionId.Should().Be(47);
        session.UserId.Should().Be("ola@cronus.example");
        session.ClientType.Should().Be("WebClient");
        session.LogOnDate.Should().Be(new DateTimeOffset(2026, 9, 20, 7, 12, 44, TimeSpan.Zero));
        session.EntryPointOperation.Should().Be("OnRun");
        session.EntryPointObjectName.Should().Be("Sales Order");
        session.EntryPointObjectId.Should().Be("42");
        session.EntryPointObjectType.Should().Be("Page");
        session.CurrentObjectName.Should().Be("Post Sales Documents");
        session.CurrentObjectId.Should().Be(82);
        session.CurrentObjectType.Should().Be("CodeUnit");
        session.CurrentOperationDuration.Should().Be(TimeSpan.FromMinutes(62),
            "the documented type is a long and names no unit, so a number is read as milliseconds");
    }

    /// <summary>
    /// The same reason every other parser here is case-insensitive: this payload mixes
    /// Microsoft's casings across endpoints, and a case-sensitive read would answer
    /// "nobody is signed in" to a tenant full of people.
    /// </summary>
    [Fact]
    public void A_row_in_another_casing_reads_the_same()
    {
        const string pascal = """
        {"value":[{"SessionId":12,"UserId":"kari@cronus.example","ClientType":"Background","LogOnDate":"2026-09-20T05:00:00Z"}]}
        """;

        var session = BcAdminClient.ParseSessions(pascal).Should().ContainSingle().Which;

        session.SessionId.Should().Be(12);
        session.UserId.Should().Be("kari@cronus.example");
        session.ClientType.Should().Be("Background");
        session.LogOnDate.Should().NotBeNull();
    }

    /// <summary>
    /// A duration can plausibly arrive either way, and the string form is what a .NET
    /// server writes a TimeSpan as. A row that says nothing about it says null, which is
    /// not the same as zero.
    /// </summary>
    [Theory]
    [InlineData("\"00:07:30\"", 450d)]
    [InlineData("\"2500\"", 2.5d)]
    [InlineData("1500", 1.5d)]
    [InlineData("0", 0d)]
    [InlineData("-5", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    public void A_duration_is_read_whichever_way_it_arrives(string raw, double? expectedSeconds)
    {
        var json = $$"""{"value":[{"sessionId":1,"currentOperationDuration":{{raw}}}]}""";

        var session = BcAdminClient.ParseSessions(json).Should().ContainSingle().Which;

        session.CurrentOperationDuration.Should().Be(
            expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
    }

    [Fact]
    public void A_row_with_no_session_id_is_skipped_because_it_could_never_be_ended()
    {
        const string json = """
        {"value":[{"userId":"nobody@cronus.example"},{"sessionId":9,"userId":"ola@cronus.example"},"not an object"]}
        """;

        BcAdminClient.ParseSessions(json).Should().ContainSingle()
            .Which.SessionId.Should().Be(9);
    }

    [Fact]
    public void An_answer_with_no_list_is_no_sessions_rather_than_a_fault()
    {
        BcAdminClient.ParseSessions("{}").Should().BeEmpty();
        BcAdminClient.ParseSessions("""{"value":null}""").Should().BeEmpty();
        BcAdminClient.ParseSessions("[]").Should().BeEmpty();
        BcAdminClient.ParseSessions("").Should().BeEmpty();
    }

    [Fact]
    public void A_body_that_is_not_json_is_a_refusal_we_can_say_out_loud()
    {
        var act = () => BcAdminClient.ParseSessions("<html>gateway timeout</html>");

        act.Should().Throw<BcApiException>()
            .WithMessage("*session list we couldn't read*");
    }

    // ── The requests ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_sessions_read_gets_the_environments_sessions_route()
    {
        var (client, handler) = Client(body: DocumentedPayload);

        var sessions = await client.ListSessionsAsync(Token, Family, Environment);

        handler.Method.Should().Be(HttpMethod.Get);
        handler.Url!.AbsolutePath.Should().EndWith("/environments/Production/sessions");
        handler.Url.AbsolutePath.Should().Contain($"/admin/{BcConstants.AdminApiVersion}/");
        sessions.Should().ContainSingle();
    }

    /// <summary>
    /// An environment Business Central no longer has cannot have anybody signed in to it,
    /// which is an answer rather than a fault - the reading the operations list gives a 404.
    /// </summary>
    [Fact]
    public async Task An_environment_that_is_gone_has_nobody_signed_in()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, "");

        (await client.ListSessionsAsync(Token, Family, Environment)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ending_a_session_deletes_that_session_id_and_sends_no_body()
    {
        var (client, handler) = Client(HttpStatusCode.NoContent, "");

        await client.CancelSessionAsync(Token, Family, Environment, 47);

        handler.Method.Should().Be(HttpMethod.Delete);
        handler.Url!.AbsolutePath.Should().EndWith("/environments/Production/sessions/47");
        handler.Body.Should().BeNull();
    }

    /// <summary>
    /// The likeliest failure of all, because the list is always a little older than the
    /// click: the session ended by itself in between. Microsoft documents no error codes
    /// of its own for this endpoint, so the status is what tells this apart.
    /// </summary>
    [Fact]
    public async Task A_session_that_has_already_ended_says_so()
    {
        var (client, _) = Client(HttpStatusCode.NotFound, "");

        var act = () => client.CancelSessionAsync(Token, Family, Environment, 47);

        (await act.Should().ThrowAsync<BcApiException>())
            .Which.Message.Should().Contain("had already ended");
    }

    [Theory]
    [InlineData("environmentNotFound", "no longer has this environment")]
    [InlineData("EnvironmentNotActive", "has to be active")]
    public async Task A_refused_cancel_is_described_by_its_code(string code, string expected)
    {
        var (client, _) = Client(HttpStatusCode.BadRequest, $$"""{"code":"{{code}}","message":"Localized prose."}""");

        var act = () => client.CancelSessionAsync(Token, Family, Environment, 47);

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.Message.Should().Contain(expected);
        thrown.Message.Should().NotContain(code, "the wire code is not what a consultant reads");
    }

    [Fact]
    public async Task A_cancel_refused_for_a_reason_microsoft_has_not_documented_still_says_what_was_asked()
    {
        var (client, _) = Client(HttpStatusCode.InternalServerError, """{"code":"somethingNew","message":"Boom."}""");

        var act = () => client.CancelSessionAsync(Token, Family, Environment, 47);

        (await act.Should().ThrowAsync<BcApiException>())
            .Which.Message.Should().Contain("ending the session").And.Contain("Boom.");
    }

    // ── The words on screen ───────────────────────────────────────────────

    /// <summary>
    /// Every client type Business Central has becomes a phrase a consultant would say out
    /// loud, and one Microsoft adds later is spaced out into words rather than reaching
    /// anybody as a wire token. The same rule the operations list follows.
    /// </summary>
    [Theory]
    [InlineData("WebClient", "Web client")]
    [InlineData("Web", "Web client")]
    [InlineData("Background", "Background")]
    [InlineData("WebServiceClient", "Web service (SOAP)")]
    [InlineData("ODataV4", "Web service (OData)")]
    [InlineData("Api", "Web service (API)")]
    [InlineData("NAS", "Job queue")]
    [InlineData("ChildSession", "Background (child session)")]
    [InlineData("SomeNewClient", "Some new client")]
    [InlineData("", "Unknown")]
    public void A_client_type_reads_as_words(string wire, string expected) =>
        BcSessionDisplay.ClientTypeWord(wire).Should().Be(expected);

    [Fact]
    public void What_a_session_is_running_names_the_object_the_way_business_central_does()
    {
        var session = BcAdminClient.ParseSessions(DocumentedPayload).Single();

        BcSessionDisplay.Doing(session).Should().Be("Post Sales Documents (code unit 82)");
    }

    [Fact]
    public void A_session_with_nothing_running_falls_back_to_where_it_came_in_and_then_to_words()
    {
        var entryOnly = BcAdminClient.ParseSessions("""
        {"value":[{"sessionId":3,"userId":"kari@cronus.example","clientType":"Background",
                   "entryPointObjectName":"Job Queue Dispatcher","entryPointObjectType":"CodeUnit","entryPointObjectId":"448"}]}
        """).Single();
        BcSessionDisplay.Doing(entryOnly).Should().Be("Job Queue Dispatcher (code unit 448)");

        var bare = BcAdminClient.ParseSessions("""{"value":[{"sessionId":4}]}""").Single();
        BcSessionDisplay.Doing(bare).Should().Be("Idle");
    }

    /// <summary>
    /// The threshold is ours and it marks a row; it never hides one and never decides
    /// anything. A session Business Central said nothing about is not long-running.
    /// </summary>
    [Fact]
    public void A_session_is_only_marked_once_it_has_been_in_one_operation_long_enough()
    {
        BcSessionDisplay.LongRunningAfter.Should().Be(TimeSpan.FromMinutes(5));

        BcSessionDisplay.IsLongRunning(Session(TimeSpan.FromMinutes(4))).Should().BeFalse();
        BcSessionDisplay.IsLongRunning(Session(TimeSpan.FromMinutes(5))).Should().BeTrue();
        BcSessionDisplay.IsLongRunning(Session(null)).Should().BeFalse();

        static BcSession Session(TimeSpan? duration) => new(
            1, "ola@cronus.example", "WebClient", DateTimeOffset.UtcNow,
            string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, null, string.Empty, duration);
    }
}
