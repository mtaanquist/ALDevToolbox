using ALDevToolbox.Services.OAuth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Persistence guarantees for the OAuth signing + encryption keys. The whole
/// point of <see cref="OAuthKeyMaterial"/> is that a process restart doesn't
/// invalidate every issued access / refresh token — so the contract is
/// "second call against the same directory returns identical keys."
///
/// Without these, the prior behaviour was <c>AddEphemeralSigningKey</c>,
/// which re-rolls keys on every startup and forces every connected Claude
/// (and every other OAuth client) to re-consent.
/// </summary>
public sealed class OAuthKeyMaterialTests : IDisposable
{
    private readonly string _dir;

    public OAuthKeyMaterialTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aldt-oauth-keys-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void First_call_generates_two_key_files()
    {
        var (signing, encryption) = OAuthKeyMaterial.LoadOrCreate(_dir, NullLogger.Instance);

        signing.Should().BeOfType<RsaSecurityKey>();
        encryption.Should().BeOfType<RsaSecurityKey>();
        File.Exists(Path.Combine(_dir, OAuthKeyMaterial.SigningFileName)).Should().BeTrue();
        File.Exists(Path.Combine(_dir, OAuthKeyMaterial.EncryptionFileName)).Should().BeTrue();
    }

    [Fact]
    public void Second_call_against_the_same_directory_returns_identical_keys()
    {
        var (firstSigning, firstEncryption) = OAuthKeyMaterial.LoadOrCreate(_dir, NullLogger.Instance);
        var (secondSigning, secondEncryption) = OAuthKeyMaterial.LoadOrCreate(_dir, NullLogger.Instance);

        // Comparing on the public modulus is sufficient — two RSA keys are
        // identical iff their (modulus, exponent) match. The private half
        // is derived from the same parameters.
        PublicModulusOf(firstSigning).Should().Equal(PublicModulusOf(secondSigning),
            "the signing key must persist across instantiations so issued tokens stay valid after a restart.");
        PublicModulusOf(firstEncryption).Should().Equal(PublicModulusOf(secondEncryption),
            "the encryption key must persist across instantiations so issued tokens stay readable after a restart.");
    }

    [Fact]
    public void Signing_and_encryption_keys_are_distinct()
    {
        // Convention is one key per purpose. Re-using a key across signing
        // and encryption is a well-known anti-pattern: a forced signature
        // over chosen ciphertext leaks key material.
        var (signing, encryption) = OAuthKeyMaterial.LoadOrCreate(_dir, NullLogger.Instance);

        PublicModulusOf(signing).Should().NotEqual(PublicModulusOf(encryption));
    }

    [Fact]
    public void Unwritable_directory_falls_back_to_in_memory_keys()
    {
        // Simulate the dev-without-DATA_PROTECTION_KEY_DIR path: hand it
        // somewhere CreateDirectory will throw. Use an obviously-invalid
        // null-byte path (cross-platform; Windows + Linux both reject it).
        var bad = "/proc/1/this-is-not-writable\0";
        var act = () => OAuthKeyMaterial.LoadOrCreate(bad, NullLogger.Instance);

        // Must NOT throw — the contract is "fall back to ephemeral, log a
        // warning, keep booting" so the app stays usable on a dev box that
        // forgot to mount the key volume.
        var (signing, encryption) = act.Should().NotThrow().Subject;
        signing.Should().BeOfType<RsaSecurityKey>();
        encryption.Should().BeOfType<RsaSecurityKey>();
    }

    private static byte[] PublicModulusOf(SecurityKey key)
    {
        var rsa = ((RsaSecurityKey)key).Rsa
            ?? System.Security.Cryptography.RSA.Create(((RsaSecurityKey)key).Parameters);
        return rsa.ExportParameters(includePrivateParameters: false).Modulus!;
    }
}
