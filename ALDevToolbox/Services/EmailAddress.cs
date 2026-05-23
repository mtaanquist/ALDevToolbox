namespace ALDevToolbox.Services;

/// <summary>
/// Shared email shape/domain helpers for the signup, invite and email-change
/// flows. Deliberately lightweight: full RFC validation isn't the goal — this
/// is the existing "non-blank, has an '@', not absurdly long" gate that every
/// entry point applied independently, pulled into one place so the rule can't
/// drift between them.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>
    /// True when the value is non-blank, contains an '@', and is no longer than
    /// <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool HasValidShape(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@') && value.Length <= MaxLength;

    /// <summary>
    /// The domain part of an email (everything after the last '@'), or
    /// <see langword="null"/> when there's no '@' or nothing follows it.
    /// </summary>
    public static string? DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..];
        return domain.Length == 0 ? null : domain;
    }
}
