using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ALDevToolbox.Services;

/// <summary>Outbound email primitive used for password reset and signup workflows.</summary>
public interface IEmailService
{
    /// <summary>True when SMTP can be resolved (either via system_settings or env vars). Pages can use this to render a "ask an admin" hint.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends an email. Throws when SMTP is not configured so misconfiguration
    /// is visible at the calling site rather than silently swallowed.
    /// </summary>
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// MailKit-based <see cref="IEmailService"/>. Resolves SMTP settings via
/// <see cref="SystemSettingsService"/> with a hybrid path (DB row preferred,
/// env vars as fallback). Updates to the SiteAdmin SMTP form take effect on
/// the next request without a restart.
///
/// Failures throw and callers decide whether to surface the error to the
/// user (forgot password) or log a warning and continue (admin notifications).
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly SystemSettingsService _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    // Cache the resolved value for the request lifetime so an
    // `IsConfiguredAsync()`-then-`SendAsync()` pair doesn't double-hit the
    // database. The service is registered scoped, so the cache lives exactly
    // one request — settings updates land on the next request.
    private bool _resolved;
    private ResolvedSmtpSettings? _cached;

    public SmtpEmailService(SystemSettingsService settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await ResolveOnceAsync(ct) is not null;

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var resolved = await ResolveOnceAsync(ct);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                "Email is not configured. Set SMTP via /site-admin/settings or the SMTP_* env vars before triggering email-driven flows.");
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(resolved.From));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        var secure = resolved.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(resolved.Host, resolved.Port, secure, ct);
        if (!string.IsNullOrEmpty(resolved.User))
        {
            await client.AuthenticateAsync(resolved.User, resolved.Password ?? string.Empty, ct);
        }
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);

        _logger.LogInformation("Sent email to {To} subject {Subject}.", toEmail, subject);
    }

    private async Task<ResolvedSmtpSettings?> ResolveOnceAsync(CancellationToken ct)
    {
        if (_resolved) return _cached;
        _cached = await _settings.ResolveSmtpAsync(ct);
        _resolved = true;
        return _cached;
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

    public static (string Subject, string HtmlBody) SiteAdminTest(string displayName)
        => ("AL Dev Toolbox SMTP test",
            $"<p>Hi {Html(displayName)},</p>"
            + "<p>This is a test from /site-admin/settings. If you're reading it, the SMTP configuration is working.</p>");

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
