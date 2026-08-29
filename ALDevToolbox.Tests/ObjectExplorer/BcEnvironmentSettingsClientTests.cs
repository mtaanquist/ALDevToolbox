using System.Net;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The environment-settings writes, which all land in a <em>customer's</em> tenant. What
/// is pinned here is the shape of what goes over the wire — a wrong field name on a write
/// is not something a read-back would catch — plus the refusal wording, which is keyed on
/// Microsoft's error codes rather than on their prose.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class BcEnvironmentSettingsClientTests
{
    private const string Token = "tok";
    private const string Family = "BusinessCentral";
    private const string Environment = "Production";

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

    [Fact]
    public async Task The_cadence_write_puts_the_value_to_the_cadence_endpoint()
    {
        var (client, handler) = Client();

        await client.SetAppUpdateCadenceAsync(Token, Family, Environment, BcAppUpdateCadence.DuringMajorUpgrade);

        handler.Method.Should().Be(HttpMethod.Put);
        handler.Url!.AbsolutePath.Should().EndWith("/environments/Production/settings/appSourceAppsUpdateCadence");
        handler.Body.Should().Contain("\"value\":\"DuringMajorUpgrade\"");
    }

    [Fact]
    public async Task The_m365_write_posts_the_flag_as_a_string()
    {
        var (client, handler) = Client();

        await client.SetM365AccessAsync(Token, Family, Environment, enabled: true);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Url!.AbsolutePath.Should().EndWith("/settings/accesswithm365licenses");
        handler.Body.Should().Contain("\"enabled\":\"true\"", "the documented body sends the boolean as text");
    }

    [Fact]
    public async Task Reading_m365_access_parses_the_flag_whatever_its_form()
    {
        var (jsonClient, _) = Client(body: """{"enabled": true}""");
        (await jsonClient.GetM365AccessAsync(Token, Family, Environment)).Should().BeTrue();

        var (stringClient, _) = Client(body: """{"enabled": "false"}""");
        (await stringClient.GetM365AccessAsync(Token, Family, Environment)).Should().BeFalse();

        var (emptyClient, _) = Client(body: "{}");
        (await emptyClient.GetM365AccessAsync(Token, Family, Environment)).Should().BeNull(
            "an environment too old to support it says nothing, which is not the same as 'off'");
    }

    [Fact]
    public async Task The_target_version_write_patches_that_version_and_selects_it()
    {
        var (client, handler) = Client();

        await client.SelectTargetVersionAsync(Token, Family, Environment, "27.6", "GA");

        handler.Method.Should().Be(HttpMethod.Patch);
        handler.Url!.AbsolutePath.Should().EndWith("/environments/Production/updates/27.6");
        handler.Body.Should().Contain("\"selected\":true").And.Contain("\"targetVersionType\":\"GA\"");
    }

    [Fact]
    public async Task The_target_version_write_omits_a_type_it_was_not_given()
    {
        var (client, handler) = Client();

        await client.SelectTargetVersionAsync(Token, Family, Environment, "27.6", null);

        handler.Body.Should().NotContain("targetVersionType",
            "the API defaults it to GA, and sending an empty one would be a different request");
        handler.Body.Should().NotContain("selectedDateTime").And.NotContain("ignoreUpdateWindow",
            "a version pick that carries no date must leave the customer's slot alone");
    }

    [Fact]
    public async Task The_date_write_sends_the_moment_in_utc_and_the_window_flag_as_a_boolean()
    {
        var (client, handler) = Client();

        await client.SelectTargetVersionAsync(
            Token, Family, Environment, "27.6", "GA",
            new DateTimeOffset(2026, 10, 29, 3, 0, 0, TimeSpan.FromHours(1)), ignoreUpdateWindow: true);

        handler.Method.Should().Be(HttpMethod.Patch);
        handler.Body.Should().Contain("\"selectedDateTime\":\"2026-10-29T02:00:00Z\"",
            "the date travels in UTC whatever offset the caller had");
        handler.Body.Should().Contain("\"ignoreUpdateWindow\":true",
            "this body already carries 'selected' as a real boolean, so both flags keep the same shape");
    }

    [Fact]
    public async Task ListTimezones_reads_the_ids_the_window_write_accepts()
    {
        const string body = """
        { "value": [
            { "id": "Romance Standard Time", "displayName": "(UTC+01:00) Brussels, Copenhagen, Madrid, Paris",
              "currentUtcOffset": "+01:00", "supportsDaylightSavingTime": true, "isCurrentlyDaylightSavingTime": true },
            { "id": "UTC", "displayName": "(UTC) Coordinated Universal Time", "currentUtcOffset": "+00:00" }
        ] }
        """;
        var (client, handler) = Client(body: body);

        var zones = await client.ListTimezonesAsync(Token);

        handler.Url!.AbsolutePath.Should().EndWith("/applications/settings/timezones");
        zones.Should().HaveCount(2);
        zones[0].Id.Should().Be("Romance Standard Time");
        zones[0].DisplayName.Should().Contain("Copenhagen");
    }

    [Fact]
    public void Timezone_parsing_tolerates_an_empty_or_malformed_envelope()
    {
        BcAdminClient.ParseTimezones("""{ "value": [] }""").Should().BeEmpty();
        BcAdminClient.ParseTimezones("{}").Should().BeEmpty();
        BcAdminClient.ParseTimezones("").Should().BeEmpty();
        var act = () => BcAdminClient.ParseTimezones("<html>502</html>");
        act.Should().Throw<BcApiException>();
    }

    [Theory]
    [InlineData("environmentNotFound", "no longer has this environment")]
    [InlineData("applicationTypeDoesNotExist", "application family")]
    public void A_refused_write_is_described_by_its_code(string code, string expected)
    {
        var body = $$"""{"code":"{{code}}","message":"Localized prose."}""";

        var message = BcAdminClient.DescribeSettingsFailure(HttpStatusCode.BadRequest, body, "setting the app update cadence");

        message.Should().Contain(expected);
        message.Should().NotContain(code, "the wire code is not what a consultant reads");
    }

    [Fact]
    public void An_unrecognised_refusal_still_names_what_was_being_done()
    {
        var message = BcAdminClient.DescribeSettingsFailure(
            HttpStatusCode.InternalServerError, """{"code":"somethingNew","message":"Boom."}""", "choosing the next update");

        message.Should().Contain("choosing the next update").And.Contain("Boom.");
    }

    [Fact]
    public async Task A_refused_write_throws_with_the_status_attached()
    {
        var (client, _) = Client(HttpStatusCode.BadRequest, """{"code":"environmentNotFound","message":"x"}""");

        var act = () => client.SetAppUpdateCadenceAsync(Token, Family, Environment, BcAppUpdateCadence.Default);

        var thrown = (await act.Should().ThrowAsync<BcApiException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("no longer has this environment");
    }

    [Fact]
    public async Task An_empty_cadence_never_reaches_the_api()
    {
        var (client, handler) = Client();

        var act = () => client.SetAppUpdateCadenceAsync(Token, Family, Environment, "  ");

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Method.Should().BeNull();
    }
}
