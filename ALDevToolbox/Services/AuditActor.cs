using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Composes the <c>"display name &lt;email&gt;"</c> string the audit log records an
/// actor as, looked up from the database rather than from claims.
///
/// <para>Why from the database: <see cref="Data.AuditInterceptor"/> reads the signing-in
/// user's claims off <c>HttpContext</c>, which a Blazor circuit and a background worker
/// both lack. Anything that records an actor outside a plain HTTP request resolves it
/// here instead, so one environment's history reads alike whichever route wrote it.</para>
/// </summary>
public static class AuditActor
{
    /// <summary>What the log calls an actor it cannot identify — the interceptor's own wording.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The user in <c>"display name &lt;email&gt;"</c> form, falling back to the display
    /// name or the email alone, and to <see cref="Unknown"/> when there is no user or the
    /// account is gone.
    /// </summary>
    public static async Task<string> ResolveAsync(AppDbContext db, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null) return Unknown;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.DisplayName, u.Email })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (user is null) return Unknown;

        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return Unknown;
        return string.IsNullOrWhiteSpace(user.Email) ? name : $"{name} <{user.Email}>";
    }
}
