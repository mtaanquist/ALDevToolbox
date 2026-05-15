using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using QRCoder;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Result of <see cref="TotpService.BeginEnrollmentAsync"/>. The Base32 secret
/// and <c>otpauth://</c> URI are the same value in two presentations; the QR
/// PNG is rendered server-side via QRCoder so we don't ship a JS QR library.
/// All three are shown to the user once during setup.
/// </summary>
public sealed record TotpEnrollment(string SecretBase32, string OtpAuthUri, byte[] QrPng);

/// <summary>
/// TOTP (RFC 6238) enrollment + verification. Secret is generated server-side
/// (20 random bytes, Base32-encoded), encrypted at rest via Data Protection
/// (purpose <see cref="TotpSecretProtectionPurpose"/>), and decrypted only to
/// run the rolling-code verifier. Confirmation is two-step — the user scans
/// the QR / types the secret into their authenticator, then sends back a
/// current code to prove the seed transferred correctly.
/// </summary>
public sealed class TotpService
{
    public const string TotpSecretProtectionPurpose = "ALDevToolbox.UserTotpSecret";
    private const string Issuer = "AL Dev Toolbox";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly RecoveryCodeService _recoveryCodes;

    public TotpService(
        AppDbContext db,
        IDataProtectionProvider protection,
        TimeProvider clock,
        RecoveryCodeService recoveryCodes)
    {
        _db = db;
        _protector = protection.CreateProtector(TotpSecretProtectionPurpose);
        _clock = clock;
        _recoveryCodes = recoveryCodes;
    }

    /// <summary>
    /// Generates a fresh secret and persists / overwrites the unconfirmed
    /// enrollment row for the user. The returned <see cref="TotpEnrollment"/>
    /// is for one-time display: the QR / Base32 lets the user scan; submitting
    /// a current code to <see cref="ConfirmEnrollmentAsync"/> flips the
    /// enrollment to active.
    /// </summary>
    public async Task<TotpEnrollment> BeginEnrollmentAsync(int userId, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var raw = RandomNumberGenerator.GetBytes(20);
        var secretBase32 = Base32Encoding.ToString(raw);
        var encrypted = _protector.Protect(secretBase32);
        var now = _clock.GetUtcNow().UtcDateTime;

        var existing = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (existing is null)
        {
            _db.UserTotpSecrets.Add(new UserTotpSecret
            {
                UserId = userId,
                SecretEncrypted = encrypted,
                CreatedAt = now,
            });
        }
        else if (existing.ConfirmedAt is null)
        {
            // Overwrite a still-unconfirmed enrollment: the user is restarting
            // setup; the prior secret never reached an authenticator.
            existing.SecretEncrypted = encrypted;
            existing.CreatedAt = now;
        }
        else
        {
            // Already confirmed — refuse to overwrite without an explicit
            // disable first, so a hijacked session can't silently rotate the
            // factor out from under the user.
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Totp"] = "TOTP is already enabled. Disable it first if you want to re-enroll."
            });
        }
        await _db.SaveChangesAsync(ct);

        var label = $"{Issuer}:{user.Email}";
        var uri = $"otpauth://totp/{Uri.EscapeDataString(label)}?secret={secretBase32}&issuer={Uri.EscapeDataString(Issuer)}";
        var qr = RenderQr(uri);
        return new TotpEnrollment(secretBase32, uri, qr);
    }

    /// <summary>
    /// Verifies a code against the still-unconfirmed enrollment; on success
    /// stamps <c>confirmed_at</c>, flips <see cref="User.TotpEnabled"/>, and
    /// issues 10 fresh recovery codes (returned for one-time display).
    /// </summary>
    public async Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(int userId, string code, CancellationToken ct = default)
    {
        var secret = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["Totp"] = "Start a TOTP enrollment first." });

        var base32 = _protector.Unprotect(secret.SecretEncrypted);
        if (!VerifyCode(base32, code))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["Code"] = "That code didn't match. Check your authenticator and try again." });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        secret.ConfirmedAt = now;
        secret.LastUsedAt = now;
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.TotpEnabled = true;
        await _db.SaveChangesAsync(ct);

        return await _recoveryCodes.RegenerateAsync(userId, ct);
    }

    /// <summary>
    /// Verifies a code against the user's confirmed TOTP secret. Returns false
    /// when there's no confirmed enrollment or the code is wrong / out of
    /// window. Stamps <c>last_used_at</c> on success.
    /// </summary>
    public async Task<bool> VerifyAsync(int userId, string code, CancellationToken ct = default)
    {
        var secret = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ConfirmedAt != null, ct);
        if (secret is null) return false;
        var base32 = _protector.Unprotect(secret.SecretEncrypted);
        if (!VerifyCode(base32, code)) return false;
        secret.LastUsedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Drops the TOTP secret and every recovery code in one step. Flips
    /// <see cref="User.TotpEnabled"/> back to false.
    /// </summary>
    public async Task DisableAsync(int userId, CancellationToken ct = default)
    {
        var secret = await _db.UserTotpSecrets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (secret is not null) _db.UserTotpSecrets.Remove(secret);
        var codes = await _db.UserRecoveryCodes.IgnoreQueryFilters()
            .Where(c => c.UserId == userId).ToListAsync(ct);
        _db.UserRecoveryCodes.RemoveRange(codes);
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        user.TotpEnabled = false;
        await _db.SaveChangesAsync(ct);
    }

    private bool VerifyCode(string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var trimmed = code.Replace(" ", "").Trim();
        if (trimmed.Length != 6 || !trimmed.All(char.IsDigit)) return false;
        var secretBytes = Base32Encoding.ToBytes(secretBase32);
        var totp = new Totp(secretBytes);
        // VerificationWindow.RfcSpecifiedNetworkDelay accepts the current step
        // and one on either side (±30s), which is RFC-recommended to handle
        // clock skew without opening a brute-force window.
        return totp.VerifyTotp(_clock.GetUtcNow().UtcDateTime, trimmed, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    private static byte[] RenderQr(string content)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(6);
    }
}
