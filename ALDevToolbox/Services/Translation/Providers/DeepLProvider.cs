using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALDevToolbox.Services.Translation.Providers;

/// <summary>
/// <see cref="IMachineTranslationProvider"/> backed by the DeepL REST API
/// (<see href="https://developers.deepl.com/docs"/>). The free and pro tiers live
/// on different hosts, distinguished by the <c>:fx</c> suffix DeepL appends to
/// free-tier keys. We pass the AL developer note / kind through DeepL's native
/// <c>context</c> parameter so domain terms translate sensibly.
/// </summary>
public sealed class DeepLProvider : IMachineTranslationProvider
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client (timeout + pooling) registered in <c>Program.cs</c>.</summary>
    public const string HttpClientName = "DeepLClient";

    private const string FreeHost = "https://api-free.deepl.com";
    private const string ProHost = "https://api.deepl.com";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ResolvedMtSettings _settings;
    private readonly HttpClient _http;
    private readonly ILogger<DeepLProvider> _logger;

    public DeepLProvider(ResolvedMtSettings settings, HttpClient http, ILogger<DeepLProvider> logger)
    {
        _settings = settings;
        _http = http;
        _logger = logger;
    }

    /// <summary>Free-tier keys end in <c>:fx</c> and use the free host; everything else is pro.</summary>
    internal static string HostForKey(string apiKey) =>
        apiKey.EndsWith(":fx", StringComparison.Ordinal) ? FreeHost : ProHost;

    public async Task<MtTestResult> TestConnectionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{HostForKey(_settings.ApiKey)}/v2/usage");
        Authorize(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            return new MtTestResult(true, "DeepL responded — the API key is valid.");
        }
        var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
        return new MtTestResult(false, $"DeepL returned {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}".Trim());
    }

    public async Task<MtTranslation> TranslateAsync(
        string sourceText, string? sourceLanguage, string targetLanguage,
        string? context, CancellationToken ct)
    {
        var payload = new DeepLRequest
        {
            Text = new[] { sourceText },
            TargetLang = ToDeepLTarget(targetLanguage),
            SourceLang = ToDeepLSource(sourceLanguage),
            Context = string.IsNullOrWhiteSpace(context) ? null : context,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{HostForKey(_settings.ApiKey)}/v2/translate")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        Authorize(req);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new HttpRequestException($"DeepL translate failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {body}".Trim());
        }

        var parsed = await resp.Content.ReadFromJsonAsync<DeepLResponse>(JsonOptions, ct).ConfigureAwait(false);
        var first = parsed?.Translations?.FirstOrDefault()
            ?? throw new HttpRequestException("DeepL returned no translations.");
        return new MtTranslation(first.Text ?? string.Empty, "DeepL", first.DetectedSourceLanguage);
    }

    private void Authorize(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _settings.ApiKey);

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 300 ? text[..300] : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Maps a BCP-47 tag to a DeepL <c>target_lang</c>. DeepL wants an uppercase
    /// code and a regional variant only for English and Portuguese (<c>EN-US</c> /
    /// <c>PT-BR</c>); Norwegian Bokmål is <c>NB</c>. Everything else is the bare
    /// uppercased language sub-tag.
    /// </summary>
    internal static string ToDeepLTarget(string targetLanguage)
    {
        var (lang, region) = SplitTag(targetLanguage);
        return lang switch
        {
            "en" => region == "GB" ? "EN-GB" : "EN-US",
            "pt" => region == "BR" ? "PT-BR" : "PT-PT",
            "nb" or "no" or "nn" => "NB",
            _ => lang.ToUpperInvariant(),
        };
    }

    /// <summary>
    /// Maps a BCP-47 tag to a DeepL <c>source_lang</c> (no regional variants), or
    /// null to let DeepL auto-detect when no source language is supplied.
    /// </summary>
    internal static string? ToDeepLSource(string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage)) return null;
        var (lang, _) = SplitTag(sourceLanguage);
        return lang switch
        {
            "nb" or "no" or "nn" => "NB",
            _ => lang.ToUpperInvariant(),
        };
    }

    private static (string Lang, string? Region) SplitTag(string tag)
    {
        var s = tag.Trim().Replace('_', '-');
        var dash = s.IndexOf('-');
        if (dash <= 0) return (s.ToLowerInvariant(), null);
        return (s[..dash].ToLowerInvariant(), s[(dash + 1)..].ToUpperInvariant());
    }

    private sealed class DeepLRequest
    {
        [JsonPropertyName("text")] public string[] Text { get; set; } = Array.Empty<string>();
        [JsonPropertyName("target_lang")] public string TargetLang { get; set; } = string.Empty;
        [JsonPropertyName("source_lang")] public string? SourceLang { get; set; }
        [JsonPropertyName("context")] public string? Context { get; set; }
    }

    private sealed class DeepLResponse
    {
        [JsonPropertyName("translations")] public List<DeepLTranslationItem>? Translations { get; set; }
    }

    private sealed class DeepLTranslationItem
    {
        [JsonPropertyName("detected_source_language")] public string? DetectedSourceLanguage { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
