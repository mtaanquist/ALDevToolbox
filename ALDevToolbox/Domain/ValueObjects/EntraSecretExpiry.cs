using System.Globalization;

namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>Where a recorded client-secret expiry date sits relative to today.</summary>
public enum EntraSecretExpiryState
{
    /// <summary>No expiry date was recorded, so nothing can be said about it.</summary>
    Unknown,
    /// <summary>Further out than <see cref="EntraSecretExpiry.WarningWindowDays"/>.</summary>
    Ok,
    /// <summary>Inside the warning window but not yet lapsed.</summary>
    Expiring,
    /// <summary>The recorded date has passed; Microsoft sign-in is probably already broken.</summary>
    Expired,
}

/// <summary>
/// How close an Entra client secret is to lapsing. Entra never tells the app
/// when a secret expires, so the date is self-reported by whoever created it
/// (a SiteAdmin for the deployment-wide registration, an org Admin for their
/// own) and this type only compares it to today.
///
/// <para>Shared by three callers — both settings pages and the org dashboard's
/// attention column — so the window and the arithmetic live in one place
/// rather than being re-derived per surface. The wording stays with each
/// caller, as the dashboard's own note requires.</para>
/// </summary>
/// <param name="State">Which side of the window the date falls on.</param>
/// <param name="DaysRemaining">
/// Whole days from today to the expiry date: 0 on the day itself, negative
/// once it has passed. Zero and meaningless when
/// <see cref="EntraSecretExpiryState.Unknown"/>.
/// </param>
public readonly record struct EntraSecretExpiry(EntraSecretExpiryState State, int DaysRemaining)
{
    /// <summary>
    /// How much notice an admin gets. Two weeks is enough to create a new
    /// secret in the Entra admin center, paste it in, and still have working
    /// sign-in if they only look at the dashboard once a week.
    /// </summary>
    public const int WarningWindowDays = 14;

    public static readonly EntraSecretExpiry Unknown = new(EntraSecretExpiryState.Unknown, 0);

    /// <summary>Classifies <paramref name="expiresAt"/> against <paramref name="today"/> (UTC).</summary>
    public static EntraSecretExpiry From(DateOnly? expiresAt, DateOnly today)
    {
        if (expiresAt is not { } date) return Unknown;
        var days = date.DayNumber - today.DayNumber;
        var state = days switch
        {
            < 0 => EntraSecretExpiryState.Expired,
            <= WarningWindowDays => EntraSecretExpiryState.Expiring,
            _ => EntraSecretExpiryState.Ok,
        };
        return new EntraSecretExpiry(state, days);
    }

    /// <summary>True when an admin should be told without having to go looking.</summary>
    public bool NeedsAttention => State is EntraSecretExpiryState.Expiring or EntraSecretExpiryState.Expired;

    /// <summary>
    /// Parses the <c>yyyy-MM-dd</c> value an <c>&lt;input type="date"&gt;</c>
    /// posts. Blank is valid and means "none recorded"; anything else that
    /// doesn't parse is a validation failure for the caller to report against
    /// its own field key.
    /// </summary>
    public static bool TryParseInput(string? raw, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return false;
        }
        date = parsed;
        return true;
    }
}
