using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Translation.Providers;

/// <summary>
/// One machine translation as a provider returns it: the translated string, the
/// provider that produced it (used as the suggestion's <c>Origin</c>), and the
/// source language the provider auto-detected when none was supplied.
/// </summary>
public sealed record MtTranslation(string TargetText, string ProviderName, string? DetectedSourceLanguage);

/// <summary>Cheap reachability + auth check result for the admin "Test connection" button.</summary>
public sealed record MtTestResult(bool Success, string Message);

/// <summary>
/// Fully resolved machine-translation configuration with the plaintext API key.
/// Built per call from the decrypted per-org settings by
/// <see cref="OrganizationConfigService.ResolveMachineTranslationAsync"/>; held
/// only inside the service boundary, never persisted, never logged.
/// </summary>
public sealed record ResolvedMtSettings(string Provider, string ApiKey, MtTrigger Trigger);

/// <summary>
/// The transport a translation backend (DeepL today; Google / Microsoft / an LLM
/// later) exposes, abstracted over the concrete vendor. There is no industry-wide
/// MT wire protocol, so swappability comes from this interface plus
/// <see cref="IMachineTranslationProviderFactory"/> — modelled on the sanctioned
/// off-site storage provider pattern in <c>Services/Offsite/</c>. Only transport
/// lives here; the trigger logic, suggestion merge and caching stay in
/// <see cref="MachineTranslationService"/> and the Translator page.
///
/// <para>
/// Instances are short-lived, built per call from the resolved (decrypted)
/// settings via the factory. Implementations reuse a pooled <c>HttpClient</c>
/// from <see cref="IHttpClientFactory"/>, so they don't own it and don't need to
/// be disposed.
/// </para>
/// </summary>
public interface IMachineTranslationProvider
{
    /// <summary>Cheap reachability + auth check against the configured account (e.g. DeepL's usage endpoint).</summary>
    Task<MtTestResult> TestConnectionAsync(CancellationToken ct);

    /// <summary>
    /// Translates <paramref name="sourceText"/> into
    /// <paramref name="targetLanguage"/>. <paramref name="sourceLanguage"/> may be
    /// null (let the provider auto-detect). <paramref name="context"/> is optional
    /// untranslated hint text (the AL developer note / kind) that providers which
    /// support it — DeepL's native <c>context</c> parameter — use to disambiguate;
    /// others ignore it.
    /// </summary>
    Task<MtTranslation> TranslateAsync(
        string sourceText, string? sourceLanguage, string targetLanguage,
        string? context, CancellationToken ct);
}
