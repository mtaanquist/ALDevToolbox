using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="SystemSettingsService"/>: the singleton
/// row is auto-created if missing, the SMTP password is encrypted at rest
/// and round-trips through Data Protection, and the hybrid SMTP resolver
/// prefers DB values over env vars.
/// </summary>
public sealed class SystemSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDataProtectionProvider _protector;

    public SystemSettingsServiceTests()
    {
        // In-memory key ring is fine for tests — we never need persistence
        // across processes. AddDataProtection() registers the default
        // provider; resolving it gives us a working IDataProtectionProvider.
        var services = new ServiceCollection();
        services.AddDataProtection();
        _protector = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Get_view_auto_creates_singleton_row_when_missing()
    {
        // The migration inserts the singleton row, but TestDb-backed tests
        // that bypass it (or future databases that don't seed it) shouldn't
        // NRE. LoadAsync inserts on demand.
        await using (var ctx = _db.NewContext())
        {
            ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
            await ctx.SaveChangesAsync();
        }

        var svc = NewService();
        var view = await svc.GetViewAsync();
        view.SmtpHost.Should().BeNull();
        view.HasSmtpPassword.Should().BeFalse();
        view.DefaultSignupAutoApprove.Should().BeFalse();
    }

    [Fact]
    public async Task Save_encrypts_smtp_password_at_rest()
    {
        var svc = NewService();
        await svc.SaveAsync(new SystemSettingsInput(
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            SmtpUser: "noreply",
            SmtpPassword: "supers3cret",
            SmtpFrom: "noreply@example.com",
            SmtpUseStartTls: true,
            BannerText: null,
            DefaultSignupAutoApprove: false));

        await using var read = _db.NewContext();
        var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
        row.SmtpPasswordEncrypted.Should().NotBeNullOrEmpty();
        row.SmtpPasswordEncrypted.Should().NotContain("supers3cret",
            "the password must be ciphertext, not plaintext");
    }

    [Fact]
    public async Task Save_with_null_password_leaves_existing_value()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(password: "first-password"));

        // Second save with SmtpPassword=null shouldn't disturb the existing
        // ciphertext — the form posts null when the field is left blank.
        await svc.SaveAsync(NewInput(password: null, host: "different.example.com"));

        var resolved = await svc.ResolveSmtpAsync();
        resolved!.Password.Should().Be("first-password");
        resolved.Host.Should().Be("different.example.com");
    }

    [Fact]
    public async Task Save_with_empty_password_clears_stored_value()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(password: "to-be-cleared"));
        await svc.SaveAsync(NewInput(password: ""));

        var view = await svc.GetViewAsync();
        view.HasSmtpPassword.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_prefers_db_settings_over_env_vars()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(host: "db.example.com", password: "db-password"));

        Environment.SetEnvironmentVariable("SMTP_HOST", "env.example.com");
        Environment.SetEnvironmentVariable("SMTP_FROM", "env@example.com");
        try
        {
            var resolved = await svc.ResolveSmtpAsync();
            resolved!.Host.Should().Be("db.example.com");
            resolved.Password.Should().Be("db-password");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMTP_HOST", null);
            Environment.SetEnvironmentVariable("SMTP_FROM", null);
        }
    }

    [Fact]
    public async Task Resolve_falls_back_to_env_vars_when_db_unset()
    {
        // Empty DB row: host left null. Env vars should be used.
        Environment.SetEnvironmentVariable("SMTP_HOST", "env.example.com");
        Environment.SetEnvironmentVariable("SMTP_FROM", "env@example.com");
        Environment.SetEnvironmentVariable("SMTP_PORT", "2525");
        try
        {
            var svc = NewService();
            // GetView creates the singleton row but with no host/from — the
            // resolver should still see the row as "unset" and fall back.
            await svc.GetViewAsync();

            var resolved = await svc.ResolveSmtpAsync();
            resolved.Should().NotBeNull();
            resolved!.Host.Should().Be("env.example.com");
            resolved.Port.Should().Be(2525);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMTP_HOST", null);
            Environment.SetEnvironmentVariable("SMTP_FROM", null);
            Environment.SetEnvironmentVariable("SMTP_PORT", null);
        }
    }

    [Fact]
    public async Task Save_validates_smtp_port_range()
    {
        var svc = NewService();
        Func<Task> bad = () => svc.SaveAsync(NewInput(port: 0));
        var ex = await bad.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SmtpPort");
    }

    [Fact]
    public async Task Save_validates_from_address()
    {
        var svc = NewService();
        Func<Task> bad = () => svc.SaveAsync(new SystemSettingsInput(
            SmtpHost: "smtp.example.com",
            SmtpPort: 587,
            SmtpUser: null,
            SmtpPassword: null,
            SmtpFrom: "not-an-email",
            SmtpUseStartTls: null,
            BannerText: null,
            DefaultSignupAutoApprove: false));
        var ex = await bad.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SmtpFrom");
    }

    private SystemSettingsService NewService()
    {
        var ctx = _db.NewContextWithAudit(NewAuditInterceptor());
        return new SystemSettingsService(ctx, _protector, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
    }

    private static Data.AuditInterceptor NewAuditInterceptor() =>
        new(new Microsoft.AspNetCore.Http.HttpContextAccessor());

    private static SystemSettingsInput NewInput(
        string? password = "p",
        string? host = "smtp.example.com",
        int? port = 587)
        => new(
            SmtpHost: host,
            SmtpPort: port,
            SmtpUser: "user",
            SmtpPassword: password,
            SmtpFrom: "noreply@example.com",
            SmtpUseStartTls: true,
            BannerText: null,
            DefaultSignupAutoApprove: false);
}
