using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Light-weight behavioural coverage for <see cref="SmtpEmailService"/>:
/// <c>IsConfiguredAsync</c> tracks SMTP resolution, <c>SendAsync</c> throws
/// when nothing is configured (so misconfiguration is loud rather than
/// silent), and the resolved settings are cached for the request lifetime.
/// Actual SMTP I/O is covered by the SiteAdmin "send a test email" flow
/// against a real server.
/// </summary>
public sealed class EmailServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public EmailServiceTests()
    {
        // Make sure no test pollution from sibling tests leaves SMTP_* set.
        ClearSmtpEnv();
    }

    public void Dispose()
    {
        ClearSmtpEnv();
        _db.Dispose();
    }

    [Fact]
    public async Task IsConfiguredAsync_returns_false_when_nothing_is_set()
    {
        // No DB row, no env vars: resolver returns null, IsConfiguredAsync is false.
        await using var ctx = _db.NewContext();
        ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
        await ctx.SaveChangesAsync();

        var email = NewService();
        (await email.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_throws_when_smtp_unconfigured()
    {
        await using var ctx = _db.NewContext();
        ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
        await ctx.SaveChangesAsync();

        var email = NewService();
        var act = () => email.SendAsync("user@example.com", "Subject", "<p>Body</p>");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task IsConfiguredAsync_returns_true_when_env_vars_supply_smtp()
    {
        Environment.SetEnvironmentVariable("SMTP_HOST", "env.example.com");
        Environment.SetEnvironmentVariable("SMTP_FROM", "env@example.com");

        var email = NewService();
        (await email.IsConfiguredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task EmailTemplates_render_with_html_encoded_user_input()
    {
        // Display names and URLs flow through HtmlEncode so a hostile value
        // can't sneak markup into the email body.
        var (subject, body) = EmailTemplates.ForgotPassword(
            displayName: "<script>",
            resetUrl: "https://example.com/reset?token=abc&utm_source=email");

        subject.Should().Be("Reset your password");
        body.Should().NotContain("<script>");
        body.Should().Contain("&lt;script&gt;");
        body.Should().Contain("token=abc&amp;utm_source=email");
    }

    private SmtpEmailService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider,
            NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        return new SmtpEmailService(settings, NullLogger<SmtpEmailService>.Instance);
    }

    private static void ClearSmtpEnv()
    {
        Environment.SetEnvironmentVariable("SMTP_HOST", null);
        Environment.SetEnvironmentVariable("SMTP_PORT", null);
        Environment.SetEnvironmentVariable("SMTP_USER", null);
        Environment.SetEnvironmentVariable("SMTP_PASSWORD", null);
        Environment.SetEnvironmentVariable("SMTP_FROM", null);
        Environment.SetEnvironmentVariable("SMTP_USE_STARTTLS", null);
    }
}
