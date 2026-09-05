namespace ALDevToolbox.Startup;

/// <summary>
/// The Translator tool: translation memory, machine translation and the
/// per-tenant provider clients.
/// </summary>
public static class TranslatorRegistration
{
    /// <summary>Registers the Translator services and their HTTP clients.</summary>
    public static IServiceCollection AddTranslator(this IServiceCollection services)
    {
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationMemoryService>();
        services.AddScoped<ALDevToolbox.Services.Translation.MachineTranslationService>();
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationSuggestionCoordinator>();
        // Fills the memory from the .xlf files in the organisation's own
        // repositories (issue #631) - nightly, and on demand from the admin
        // Translation memory page.
        services.AddScoped<ALDevToolbox.Services.Translation.TranslationMemoryIngestService>();
        services.AddSingleton<ALDevToolbox.Services.Translation.Providers.IMachineTranslationProviderFactory,
            ALDevToolbox.Services.Translation.Providers.MachineTranslationProviderFactory>();
        // DeepL machine-translation client (per-tenant BYOK). Fixed public hosts
        // (api.deepl.com / api-free.deepl.com) so no SSRF guard is needed; just a
        // bounded timeout. The provider sets the host and auth header per request.
        services.AddHttpClient(ALDevToolbox.Services.Translation.Providers.DeepLProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}
