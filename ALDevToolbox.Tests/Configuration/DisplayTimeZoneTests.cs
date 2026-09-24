using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The organisation's display time zone (issue #942): the setting on
/// Administration → Identity, and the per-circuit formatter every
/// <c>&lt;Timestamp&gt;</c> reads it through.
/// </summary>
public sealed class DisplayTimeZoneTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private DisplayTimeZone NewDisplayTimeZone(IOrganizationContext? org = null) =>
        new(_db.NewContextFactory(), org ?? _db.OrgContext, NullLogger<DisplayTimeZone>.Instance);

    private async Task<string?> StoredZoneAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => s.DisplayTimeZoneId)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Saving_a_known_zone_stores_it()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        await svc.SetDisplayTimeZoneAsync("Europe/Copenhagen");

        (await StoredZoneAsync()).Should().Be("Europe/Copenhagen");
        var view = await _db.NewOrganizationAdminService(_db.NewContext()).GetIdentityViewAsync();
        view.DisplayTimeZoneId.Should().Be("Europe/Copenhagen", "the Identity tab opens on the saved zone");
    }

    [Fact]
    public async Task Saving_an_unknown_zone_is_refused_with_the_field_key()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SetDisplayTimeZoneAsync("Europe/Atlantis");

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(OrganizationSettings.DisplayTimeZoneId));
        (await StoredZoneAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UTC")]
    public async Task Null_blank_or_UTC_clears_the_zone_back_to_the_default(string? value)
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());
        await svc.SetDisplayTimeZoneAsync("Europe/Copenhagen");

        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync(value);

        (await StoredZoneAsync()).Should().BeNull();
        NewDisplayTimeZone().Zone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task Changing_the_zone_is_audited()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = _db.NewOrganizationAdminService(ctx);

        await svc.SetDisplayTimeZoneAsync("Europe/Copenhagen");
        await svc.SetDisplayTimeZoneAsync("America/New_York");

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.OrganizationSettings)
            .OrderBy(a => a.Id)
            .ToListAsync();
        rows.Should().HaveCountGreaterThanOrEqualTo(2, "each change to the setting writes an audit row");
        rows.Last().SnapshotJson.Should().Contain("Europe/Copenhagen",
            "the update's snapshot records the zone it replaced");
    }

    [Fact]
    public async Task Times_are_UTC_when_no_zone_is_set()
    {
        var tz = NewDisplayTimeZone();

        await tz.EnsureLoadedAsync();

        tz.Zone.Should().Be(TimeZoneInfo.Utc);
        tz.Format(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc), "yyyy-MM-dd HH:mm")
            .Should().Be("2026-03-29 00:30");
    }

    [Fact]
    public async Task Times_are_UTC_when_no_organisation_is_in_scope()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        var tz = NewDisplayTimeZone(new AmbientOrganizationContext());

        await tz.EnsureLoadedAsync();

        tz.Zone.Should().Be(TimeZoneInfo.Utc, "a pre-login page or a background worker has no organisation to ask");
    }

    [Fact]
    public async Task Copenhagen_converts_across_the_spring_DST_boundary()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        var tz = NewDisplayTimeZone();
        await tz.EnsureLoadedAsync();

        // Clocks go forward at 01:00 UTC on 2026-03-29: +01:00 before, +02:00 after.
        tz.Format(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc), "HH:mm").Should().Be("01:30");
        tz.Format(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), "HH:mm").Should().Be("03:30");
    }

    [Fact]
    public async Task An_unspecified_kind_is_read_as_UTC()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");
        var tz = NewDisplayTimeZone();
        await tz.EnsureLoadedAsync();

        // What EF hands back for a timestamp column.
        var fromDb = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Unspecified);

        tz.ToDisplay(fromDb).Should().Be(new DateTime(2026, 3, 29, 1, 30, 0));
        tz.UtcTooltip(fromDb).Should().Be("2026-03-29 00:30:00 UTC");
    }

    [Fact]
    public async Task The_zone_is_read_once_per_circuit_until_invalidated()
    {
        var tz = NewDisplayTimeZone();
        await tz.EnsureLoadedAsync();
        tz.Zone.Should().Be(TimeZoneInfo.Utc);

        await _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync("Europe/Copenhagen");

        tz.Zone.Should().Be(TimeZoneInfo.Utc, "the zone is cached for the life of the circuit");
        tz.Invalidate();
        (await tz.EnsureLoadedAsync()).Id.Should().Be("Europe/Copenhagen");
    }

    [Fact]
    public void Selectable_zones_are_IANA_ids_with_their_offset_in_the_label()
    {
        var zones = DisplayTimeZone.SelectableZones();

        zones.Should().NotBeEmpty("the host needs a time zone database for this setting to work");
        zones.Should().OnlyContain(z => z.Id.Contains('/') && !z.Id.StartsWith("Etc/"));
        zones.Select(z => z.Id).Should().Contain("Europe/Copenhagen");

        var copenhagen = zones.Single(z => z.Id == "Europe/Copenhagen");
        DisplayTimeZone.Label(copenhagen, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc))
            .Should().Be("Europe/Copenhagen (UTC+02:00)");
        DisplayTimeZone.Label(copenhagen, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))
            .Should().Be("Europe/Copenhagen (UTC+01:00)");
        var newYork = zones.Single(z => z.Id == "America/New_York");
        DisplayTimeZone.Label(newYork, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))
            .Should().Be("America/New_York (UTC-05:00)");
    }
}
