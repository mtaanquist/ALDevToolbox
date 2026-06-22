namespace ALDevToolbox.Services.Translation.Providers;

/// <summary>
/// Selects and constructs the concrete <see cref="IMachineTranslationProvider"/>
/// for a request from the resolved per-org settings. Singleton + stateless,
/// mirroring <c>OffsiteStorageProviderFactory</c>; the providers it makes are the
/// short-lived per-call objects. DeepL is the only backend today — a second
/// vendor is a new <c>case</c> here plus its provider class.
/// </summary>
public interface IMachineTranslationProviderFactory
{
    IMachineTranslationProvider Create(ResolvedMtSettings settings);
}

/// <inheritdoc cref="IMachineTranslationProviderFactory" />
public sealed class MachineTranslationProviderFactory : IMachineTranslationProviderFactory
{
    /// <summary>Discriminator for the DeepL backend (the default and only one today).</summary>
    public const string DeepLProviderKey = "deepl";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggers;

    public MachineTranslationProviderFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggers)
    {
        _httpClientFactory = httpClientFactory;
        _loggers = loggers;
    }

    public IMachineTranslationProvider Create(ResolvedMtSettings settings) => settings.Provider switch
    {
        // Unknown / blank discriminators fall back to DeepL rather than failing
        // the resolve — same defensive posture as the off-site factory's S3 default.
        DeepLProviderKey => NewDeepL(settings),
        _ => NewDeepL(settings),
    };

    private DeepLProvider NewDeepL(ResolvedMtSettings settings) =>
        new(settings, _httpClientFactory.CreateClient(DeepLProvider.HttpClientName), _loggers.CreateLogger<DeepLProvider>());
}
