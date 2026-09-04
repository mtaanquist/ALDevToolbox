namespace ALDevToolbox.Services.Configuration;

/// <summary>
/// The SMTP configuration a deployment can supply before an admin has filled in
/// the SiteAdmin form. <see cref="SystemSettingsService"/> prefers the
/// <c>system_settings</c> row and falls back to this.
///
/// <para>Read once at startup rather than per call, so a test can hand the
/// service its own values instead of mutating the process environment and
/// racing every other test that does the same (#733). The variable names are
/// unchanged; they are the documented deployment interface.</para>
///
/// <para><see cref="PasswordFile"/> is a path, not the secret: the password is
/// read from that file at resolve time so it never sits in the process
/// environment where a crash dump or a child process could pick it up.</para>
/// </summary>
public sealed record SmtpFallbackOptions
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? User { get; init; }

    /// <summary>Path to a file holding the password. <c>SMTP_PASSWORD_FILE</c>.</summary>
    public string? PasswordFile { get; init; }

    public string? From { get; init; }
    public string? FromName { get; init; }
    public bool? UseStartTls { get; init; }

    /// <summary>Reads the deployment's <c>SMTP_*</c> values from configuration.</summary>
    public static SmtpFallbackOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Host = Blank(configuration["SMTP_HOST"]),
        Port = int.TryParse(configuration["SMTP_PORT"], out var port) ? port : null,
        User = Blank(configuration["SMTP_USER"]),
        PasswordFile = Blank(configuration["SMTP_PASSWORD_FILE"]),
        From = Blank(configuration["SMTP_FROM"]),
        FromName = Blank(configuration["SMTP_FROM_NAME"]),
        UseStartTls = ParseBool(configuration["SMTP_USE_STARTTLS"]),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? ParseBool(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
