using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ALDevToolbox.Services;

/// <summary>Outbound email primitive used for password reset and signup workflows.</summary>
public interface IEmailService
{
    /// <summary>True when SMTP env vars are populated. Pages can use this to render a "ask an admin" hint.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends an email. Throws when SMTP is not configured so misconfiguration
    /// is visible at the calling site rather than silently swallowed.
    /// </summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// MailKit-based <see cref="IEmailService"/>. Reads SMTP_HOST, SMTP_PORT,
/// SMTP_USER, SMTP_PASSWORD_FILE, SMTP_FROM, SMTP_USE_STARTTLS from the env.
/// See <c>.design/auth-and-audit.md</c> for the contract: failures throw and
/// callers decide whether to surface the error to the user (forgot password)
/// or log a warning and continue (admin notifications).
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly ILogger<SmtpEmailService> _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _password;
    private readonly string? _from;
    private readonly bool _useStartTls;

    public SmtpEmailService(ILogger<SmtpEmailService> logger)
    {
        _logger = logger;
        _host = Environment.GetEnvironmentVariable("SMTP_HOST");
        _port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
        _user = Environment.GetEnvironmentVariable("SMTP_USER");
        _password = ReadSecret("SMTP_PASSWORD_FILE");
        _from = Environment.GetEnvironmentVariable("SMTP_FROM");
        _useStartTls = (Environment.GetEnvironmentVariable("SMTP_USE_STARTTLS") ?? "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_from);

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Email is not configured. Set SMTP_HOST, SMTP_FROM and credentials before triggering email-driven flows.");
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_from));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        var secure = _useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(_host, _port, secure, ct);
        if (!string.IsNullOrEmpty(_user))
        {
            await client.AuthenticateAsync(_user, _password ?? string.Empty, ct);
        }
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);

        _logger.LogInformation("Sent email to {To} subject {Subject}.", toEmail, subject);
    }

    private static string? ReadSecret(string envVarName)
    {
        var path = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return File.ReadAllText(path).Trim();
    }
}

/// <summary>
/// Renders the three transactional email bodies. Bodies are simple HTML
/// strings — Razor partials are an option once the templates need real
/// design, but the M13 contract is "send a working link", not "look pretty".
/// </summary>
public static class EmailTemplates
{
    public static (string Subject, string HtmlBody) ForgotPassword(string displayName, string resetUrl)
        => ("Reset your password",
            $"<p>Hi {Html(displayName)},</p>"
            + "<p>Someone (hopefully you) asked to reset your AL Dev Toolbox password. "
            + $"Use this link within the next hour to choose a new one:</p>"
            + $"<p><a href=\"{Html(resetUrl)}\">{Html(resetUrl)}</a></p>"
            + "<p>If you didn't request this, you can ignore this message and your password stays unchanged.</p>");

    public static (string Subject, string HtmlBody) SignupPending(string adminName, string requesterEmail, string orgName, string adminUsersUrl)
        => ($"New signup pending in {orgName}",
            $"<p>Hi {Html(adminName)},</p>"
            + $"<p><strong>{Html(requesterEmail)}</strong> has asked to join <strong>{Html(orgName)}</strong>. "
            + "They can't sign in until you approve.</p>"
            + $"<p><a href=\"{Html(adminUsersUrl)}\">Review pending users</a></p>");

    public static (string Subject, string HtmlBody) SignupDecided(string displayName, string orgName, bool approved, string loginUrl)
        => approved
            ? ($"You're in: {orgName}",
                $"<p>Hi {Html(displayName)},</p>"
                + $"<p>Your signup for <strong>{Html(orgName)}</strong> has been approved. You can now sign in:</p>"
                + $"<p><a href=\"{Html(loginUrl)}\">{Html(loginUrl)}</a></p>")
            : ($"Signup declined: {orgName}",
                $"<p>Hi {Html(displayName)},</p>"
                + $"<p>Your signup request for <strong>{Html(orgName)}</strong> has been declined. "
                + "If you think this is a mistake, please reach out to the organisation's administrator directly.</p>");

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
