namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// WebAuthn / passkey credential registered for a user. One user can have
/// many; each is uniquely identified globally by <see cref="CredentialId"/>
/// (the byte string returned by the authenticator). A successful assertion
/// satisfies password + 2FA in a single step — passkey is full auth.
/// </summary>
public class UserPasskey
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Raw credential id returned by the authenticator; unique across all users.</summary>
    public byte[] CredentialId { get; set; } = Array.Empty<byte>();

    /// <summary>CBOR-encoded public key, opaque to us — Fido2NetLib stores/verifies.</summary>
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    /// <summary>Anti-clone counter. Updated on every successful assertion; non-monotonic increments are rejected.</summary>
    public long SignCounter { get; set; }

    /// <summary>Comma-joined transport hints (<c>usb,nfc,internal,…</c>); informational only.</summary>
    public string Transports { get; set; } = string.Empty;

    /// <summary>Authenticator AAGUID — lets the UI render "YubiKey 5"-style labels when known.</summary>
    public Guid? Aaguid { get; set; }

    /// <summary>User-supplied nickname rendered in /account ("Work YubiKey").</summary>
    public string Nickname { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
