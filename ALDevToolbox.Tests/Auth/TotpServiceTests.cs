using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// TOTP enrolment, confirmation, verification, and disable. Uses an in-process
/// IDataProtectionProvider so secret encryption round-trips inside the test.
/// </summary>
public sealed class TotpServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private RecoveryCodeService NewRecovery(AppDbContext ctx) => new(ctx, _clock);
    private TotpService NewTotp(AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, _clock, NewRecovery(ctx));

    private async Task<int> SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = 7000,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "totp@example.com",
            PasswordHash = "x",
            DisplayName = "TOTP User",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await ctx.SaveChangesAsync();
        return 7000;
    }

    [Fact]
    public async Task Begin_enrollment_persists_encrypted_secret_but_does_not_enable_totp()
    {
        var userId = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        var totp = NewTotp(ctx);
        var enrollment = await totp.BeginEnrollmentAsync(userId);

        enrollment.SecretBase32.Should().NotBeNullOrEmpty();
        enrollment.OtpAuthUri.Should().StartWith("otpauth://totp/");
        enrollment.QrPng.Length.Should().BeGreaterThan(100);

        await using var read = _db.NewContext();
        var row = await read.UserTotpSecrets.IgnoreQueryFilters().SingleAsync(s => s.UserId == userId);
        row.ConfirmedAt.Should().BeNull();
        row.SecretEncrypted.Should().NotBe(enrollment.SecretBase32, "secret is encrypted at rest");
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_with_valid_code_enables_totp_and_returns_recovery_codes()
    {
        var userId = await SeedUserAsync();
        TotpEnrollment enrollment;
        await using (var ctx = _db.NewContext())
        {
            enrollment = await NewTotp(ctx).BeginEnrollmentAsync(userId);
        }

        var validCode = new Totp(Base32Encoding.ToBytes(enrollment.SecretBase32))
            .ComputeTotp(_clock.GetUtcNow().UtcDateTime);

        IReadOnlyList<string> codes;
        await using (var ctx = _db.NewContext())
        {
            codes = await NewTotp(ctx).ConfirmEnrollmentAsync(userId, validCode);
        }

        codes.Should().HaveCount(10);
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeTrue();
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == userId))
            .Should().Be(10);
    }

    [Fact]
    public async Task Confirm_with_wrong_code_does_not_enable_totp()
    {
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewTotp(ctx).BeginEnrollmentAsync(userId);
        }
        await using var ctx2 = _db.NewContext();
        var act = () => NewTotp(ctx2).ConfirmEnrollmentAsync(userId, "000000");
        await act.Should().ThrowAsync<PlanValidationException>();
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Disable_clears_secret_recovery_codes_and_flag()
    {
        var userId = await SeedUserAsync();
        TotpEnrollment enrollment;
        await using (var ctx = _db.NewContext())
        {
            enrollment = await NewTotp(ctx).BeginEnrollmentAsync(userId);
            var code = new Totp(Base32Encoding.ToBytes(enrollment.SecretBase32))
                .ComputeTotp(_clock.GetUtcNow().UtcDateTime);
            await NewTotp(ctx).ConfirmEnrollmentAsync(userId, code);
        }
        await using (var ctx = _db.NewContext())
        {
            await NewTotp(ctx).DisableAsync(userId);
        }
        await using var read = _db.NewContext();
        (await read.UserTotpSecrets.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)).Should().Be(0);
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == userId)).Should().Be(0);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).TotpEnabled.Should().BeFalse();
    }
}
