namespace ALDevToolbox.Startup;

/// <summary>
/// Where the boot-time key material lives, and the logger it reports through.
/// Both the OAuth signing/encryption keys and the deployment id are loaded
/// before the host is built, so neither can use the app's logger factory.
/// </summary>
internal static class BootKeyPaths
{
    /// <summary>
    /// Persistent key directory — the same app-keys volume as the Data
    /// Protection ring, with OAUTH_KEY_DIR available for operators who want to
    /// keep the OAuth keys somewhere else.
    /// </summary>
    internal static string KeyDirectory() =>
        Environment.GetEnvironmentVariable("OAUTH_KEY_DIR")
        ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY_DIR")
        ?? "/var/lib/aldevtoolbox/dp-keys";

    /// <summary>
    /// Console logger used while loading key material at boot. Both loads share
    /// this category, as they did when they sat inline in Program.cs.
    /// </summary>
    internal static ILogger KeyMaterialLogger() =>
        LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.UseUtcTimestamp = true; }))
            .CreateLogger(typeof(ALDevToolbox.Services.OAuth.OAuthKeyMaterial).FullName!);
}
