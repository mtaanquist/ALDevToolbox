namespace ALDevToolbox.Services.Offsite;

/// <summary>
/// Builds the <see cref="IOffsiteStorageProvider"/> for a given set of
/// resolved (decrypted) off-site settings. Lives behind an interface so the
/// provider selection is the one place that knows about both backends, and so
/// tests can assert the discriminator routes correctly.
/// </summary>
public interface IOffsiteStorageProviderFactory
{
    IOffsiteStorageProvider Create(ResolvedOffsiteSettings settings);
}

/// <summary>
/// Default factory. Stateless apart from the logger factory, so it registers
/// as a singleton; the providers it returns are short-lived and constructed
/// per call from per-request decrypted credentials, so they are never
/// themselves DI-registered. Unknown / blank provider values fall back to S3,
/// matching the <c>offsite_provider</c> column default.
/// </summary>
public sealed class OffsiteStorageProviderFactory : IOffsiteStorageProviderFactory
{
    /// <summary>Discriminator value for the Azure Blob backend (matches the column).</summary>
    public const string AzureBlobProviderKey = "azure-blob";

    private readonly ILoggerFactory _loggers;

    public OffsiteStorageProviderFactory(ILoggerFactory loggers)
    {
        _loggers = loggers;
    }

    public IOffsiteStorageProvider Create(ResolvedOffsiteSettings settings) => settings.Provider switch
    {
        AzureBlobProviderKey => new AzureBlobProvider(settings, _loggers.CreateLogger<AzureBlobProvider>()),
        _ => new S3Provider(settings, _loggers.CreateLogger<S3Provider>()),
    };
}
