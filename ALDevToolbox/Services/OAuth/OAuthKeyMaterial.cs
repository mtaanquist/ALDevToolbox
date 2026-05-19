using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Persistent RSA keys for OpenIddict's signing and encryption operations.
/// Without these, the server uses <c>AddEphemeralSigningKey</c>: every
/// process restart re-rolls the keys and instantly invalidates every issued
/// access and refresh token, which forces every connected assistant to
/// re-consent on the next tool call. For an internal tool that restarts
/// often (dev rebuilds, container redeploys) that's an annoying papercut.
///
/// We persist the keys alongside the existing Data Protection key ring on
/// the <c>app-keys</c> volume. Losing that volume already invalidates auth
/// cookies and the <c>system_settings</c> SMTP ciphertext; OAuth keys
/// sharing its fate isn't a new failure mode. If the directory isn't
/// writable (typical when a dev forgot to set
/// <c>DATA_PROTECTION_KEY_DIR</c>) we mirror the
/// <c>Program.cs</c> behaviour and fall back to in-memory keys with a
/// warning — same blast radius as the old ephemeral path.
/// </summary>
public static class OAuthKeyMaterial
{
    public const string SigningFileName = "oauth-signing.key";
    public const string EncryptionFileName = "oauth-encryption.key";

    /// <summary>
    /// Loads existing signing + encryption keys from <paramref name="directory"/>,
    /// or generates and persists a fresh pair when they don't exist yet.
    /// Returns ephemeral, in-memory keys (logged as a warning) when the
    /// directory isn't writable — so a dev machine without a configured key
    /// directory still boots, and just loses the persistence benefit.
    /// </summary>
    public static (SecurityKey Signing, SecurityKey Encryption) LoadOrCreate(string directory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var signingRsa = LoadOrGenerate(Path.Combine(directory, SigningFileName), logger);
            var encryptionRsa = LoadOrGenerate(Path.Combine(directory, EncryptionFileName), logger);
            return (
                new RsaSecurityKey(signingRsa) { KeyId = "aldt-oauth-signing" },
                new RsaSecurityKey(encryptionRsa) { KeyId = "aldt-oauth-encryption" });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            // Anything that means "can't put a file here" — permission
            // denied, read-only FS, invalid path, unsupported scheme —
            // falls back to ephemeral. The blast radius matches what
            // shipped before persistence existed: one extra consent
            // round-trip per active OAuth client after a restart.
            logger.LogWarning(ex,
                "OAuth key directory '{Directory}' not writable. Falling back to in-memory keys; OAuth tokens will not survive a process restart.",
                directory);
            return (
                new RsaSecurityKey(RSA.Create(2048)) { KeyId = "aldt-oauth-signing-ephemeral" },
                new RsaSecurityKey(RSA.Create(2048)) { KeyId = "aldt-oauth-encryption-ephemeral" });
        }
    }

    private static RSA LoadOrGenerate(string path, ILogger logger)
    {
        var rsa = RSA.Create(2048);
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            rsa.ImportRSAPrivateKey(bytes, out _);
            logger.LogInformation("Loaded persistent OAuth key from {Path}.", path);
            return rsa;
        }

        var fresh = rsa.ExportRSAPrivateKey();
        File.WriteAllBytes(path, fresh);
        // Tighten file perms to owner-rw on Unix. Windows inherits the
        // directory ACL — typically already restrictive on the production
        // app-keys volume.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Permissions tightening is best-effort; logging is the
                // appropriate response. The key file is still written.
                logger.LogWarning(ex, "Could not tighten file permissions on {Path}.", path);
            }
        }
        logger.LogInformation("Generated and persisted new OAuth key at {Path}.", path);
        return rsa;
    }
}
