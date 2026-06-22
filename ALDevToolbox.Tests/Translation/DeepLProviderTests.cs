using System.Net;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Translation.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translation;

/// <summary>
/// Exercises <see cref="DeepLProvider"/>'s request shaping and response parsing
/// against a stubbed <see cref="HttpMessageHandler"/> — no live network. Guards
/// the free/pro host split, the auth header, the BCP-47 → DeepL language mapping,
/// and the context pass-through.
/// </summary>
public sealed class DeepLProviderTests
{
    [Fact]
    public async Task Translate_uses_free_host_for_fx_key_and_shapes_the_request()
    {
        var handler = new StubHandler(OkJson("{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"Hej\"}]}"));
        var provider = NewProvider(handler, apiKey: "abc-123:fx");

        var result = await provider.TranslateAsync("Hello", "en-US", "da-DK", "a friendly greeting", default);

        result.TargetText.Should().Be("Hej");
        result.ProviderName.Should().Be("DeepL");
        result.DetectedSourceLanguage.Should().Be("EN");

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api-free.deepl.com/v2/translate");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("DeepL-Auth-Key");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("abc-123:fx");
        handler.LastBody.Should().Contain("\"target_lang\":\"DA\"");
        handler.LastBody.Should().Contain("\"source_lang\":\"EN\"");
        handler.LastBody.Should().Contain("\"context\":\"a friendly greeting\"");
    }

    [Fact]
    public async Task Translate_uses_pro_host_for_non_fx_key()
    {
        var handler = new StubHandler(OkJson("{\"translations\":[{\"text\":\"Hallo\"}]}"));
        var provider = NewProvider(handler, apiKey: "pro-key-no-suffix");

        await provider.TranslateAsync("Hello", null, "de-DE", null, default);

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.deepl.com/v2/translate");
        // No source language supplied → DeepL auto-detects (source_lang omitted/null).
        handler.LastBody.Should().Contain("\"target_lang\":\"DE\"");
    }

    [Theory]
    [InlineData("en-GB", "EN-GB")]
    [InlineData("en-US", "EN-US")]
    [InlineData("pt-BR", "PT-BR")]
    [InlineData("nb-NO", "NB")]
    [InlineData("sv-SE", "SV")]
    public async Task Translate_maps_target_language_to_deepl_codes(string bcp47, string expected)
    {
        var handler = new StubHandler(OkJson("{\"translations\":[{\"text\":\"x\"}]}"));
        var provider = NewProvider(handler, apiKey: "k:fx");

        await provider.TranslateAsync("Hello", null, bcp47, null, default);

        handler.LastBody.Should().Contain($"\"target_lang\":\"{expected}\"");
    }

    [Fact]
    public async Task Translate_throws_on_non_success_status()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"Authorization failure\"}"),
            ReasonPhrase = "Forbidden",
        });
        var provider = NewProvider(handler, apiKey: "bad:fx");

        var act = () => provider.TranslateAsync("Hello", "en-US", "da-DK", null, default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task TestConnection_reports_success_on_2xx_usage()
    {
        var handler = new StubHandler(OkJson("{\"character_count\":42,\"character_limit\":500000}"));
        var provider = NewProvider(handler, apiKey: "k:fx");

        var result = await provider.TestConnectionAsync(default);

        result.Success.Should().BeTrue();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api-free.deepl.com/v2/usage");
    }

    private static DeepLProvider NewProvider(StubHandler handler, string apiKey) =>
        new(new ResolvedMtSettings("deepl", apiKey, MtTrigger.OnDemand),
            new HttpClient(handler),
            NullLogger<DeepLProvider>.Instance);

    private static HttpResponseMessage OkJson(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _response;
        }
    }
}
