using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins 20260807000000_RepairPerTenantRetentionDefault, and the reason it was
/// needed at all.
/// </summary>
/// <remarks>
/// Two columns were added with <c>defaultValue: 0</c> while their entity
/// defaults said 30 and 90 and their validation demanded 1..365 / 1..3650. The
/// singleton row is created by migration, so it kept the 0 - and because
/// <see cref="SettingsInputBuilder"/> carries every stored field into every
/// tab's save, one out-of-range column made *every* settings tab unsaveable on
/// a fresh install, complaining about a field that tab does not show.
///
/// The second test is the one that matters: it fails the moment any column
/// picks up a stored default its own validation rejects, whatever the column.
/// </remarks>
public sealed class RetentionDefaultRepairMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_migrated_row_has_retention_values_its_own_rules_accept()
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);

        row.PerTenantBackupRetentionCount.Should().BeInRange(1, 365);
        row.OffsiteRetentionDays.Should().BeInRange(1, 3650);
    }

    [Fact]
    public async Task A_freshly_migrated_database_can_save_a_settings_tab_untouched()
    {
        // Exactly what an admin does on a new install: open a tab, change the
        // one field it owns, press Save. Nothing else has ever been set.
        var svc = NewService();
        var current = await svc.GetViewAsync();

        var form = new FormCollection(new()
        {
            ["BannerText"] = new StringValues("Back on Monday."),
        });

        var save = async () => await svc.SaveAsync(SettingsInputBuilder.WithGeneral(current, form));

        await save.Should().NotThrowAsync(
            "a stored value no tab shows must not be able to reject that tab's save");
        (await svc.GetViewAsync()).BannerText.Should().Be("Back on Monday.");
    }

    private SystemSettingsService NewService() => new(
        _db.NewContextWithAudit(TestDb.NewAuditInterceptor()),
        _db.DataProtectionProvider,
        NullLogger<SystemSettingsService>.Instance,
        TimeProvider.System);
}
