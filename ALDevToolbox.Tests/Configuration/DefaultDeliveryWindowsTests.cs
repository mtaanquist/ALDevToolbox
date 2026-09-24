using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The organisation's default delivery windows for new Production and Sandbox
/// environments (issue #962): the setting itself, as Administration → Business Central
/// saves it. What discovery does with it is in <c>ProjectConnectionServiceTests</c>.
/// </summary>
public sealed class DefaultDeliveryWindowsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<OrganizationSettings?> StoredAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task Both_pairs_are_stored()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        await svc.SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(
            new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(18, 0), new TimeOnly(20, 0)));

        var row = await StoredAsync();
        row!.DefaultDeliveryWindowProductionStart.Should().Be(new TimeOnly(22, 0));
        row.DefaultDeliveryWindowProductionEnd.Should().Be(new TimeOnly(6, 0));
        row.DefaultDeliveryWindowSandboxStart.Should().Be(new TimeOnly(18, 0));
        row.DefaultDeliveryWindowSandboxEnd.Should().Be(new TimeOnly(20, 0));
    }

    [Fact]
    public async Task Clearing_a_pair_sets_it_back_to_any_time()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(
            new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(18, 0), new TimeOnly(20, 0)));

        await _db.NewOrganizationAdminService(_db.NewContext()).SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(
            null, null, new TimeOnly(18, 0), new TimeOnly(20, 0)));

        var row = await StoredAsync();
        row!.DefaultDeliveryWindowProductionStart.Should().BeNull();
        row.DefaultDeliveryWindowProductionEnd.Should().BeNull();
        row.DefaultDeliveryWindowSandboxStart.Should().Be(new TimeOnly(18, 0), "the other pair is left as it was");
    }

    [Fact]
    public async Task Half_a_pair_is_refused_under_that_pairs_key_and_nothing_is_saved()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(
            new TimeOnly(22, 0), null, new TimeOnly(18, 0), new TimeOnly(20, 0)));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().BeEquivalentTo(["DefaultDeliveryWindowProduction"]);
        var row = await StoredAsync();
        row?.DefaultDeliveryWindowSandboxStart.Should().BeNull("a refused save writes neither pair");
    }

    [Fact]
    public async Task Each_half_filled_pair_gets_its_own_error()
    {
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(
            null, new TimeOnly(6, 0), new TimeOnly(18, 0), null));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().BeEquivalentTo(["DefaultDeliveryWindowProduction", "DefaultDeliveryWindowSandbox"]);
    }

    [Fact]
    public async Task Changing_a_default_is_audited()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = _db.NewOrganizationAdminService(ctx);

        await svc.SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(new TimeOnly(22, 0), new TimeOnly(6, 0), null, null));
        await svc.SetDefaultDeliveryWindowsAsync(new DefaultDeliveryWindows(new TimeOnly(23, 0), new TimeOnly(5, 0), null, null));

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.OrganizationSettings)
            .OrderBy(a => a.Id)
            .ToListAsync();
        rows.Should().HaveCountGreaterThanOrEqualTo(2, "each change to the setting writes an audit row");
        rows.Last().SnapshotJson.Should().Contain("22:00",
            "the update's snapshot records the window it replaced");
    }
}
