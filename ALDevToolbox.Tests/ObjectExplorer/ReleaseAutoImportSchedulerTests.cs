using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Target-selection for the daily release auto-import sweep
/// (<see cref="ReleaseAutoImportScheduler.ResolveTargetsAsync"/>). Pins the
/// single-tenant fix from issue #518: the system org must be swept when it's
/// the one working org (single-tenant), but skipped otherwise.
/// </summary>
public sealed class ReleaseAutoImportSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task System_org_is_only_a_target_when_single_tenant_mode_includes_it()
    {
        // Org 1 is the migration-stamped system org; enable auto-import on it.
        await EnableAutoImportAsync(TestDb.DefaultOrgId, "dk");

        await using var ctx = _db.NewContext();

        // Multi-tenant (includeSystemOrg: false): the system org is skipped —
        // this is the bug that left single-tenant installs never importing.
        var multiTenant = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
            ctx, includeSystemOrg: false, default);
        multiTenant.Should().NotContain(t => t.OrganizationId == TestDb.DefaultOrgId,
            "the system org is only the template source in a multi-tenant deployment");

        // Single-tenant (includeSystemOrg: true): the system org is the one
        // working org, so it must be swept.
        var singleTenant = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
            ctx, includeSystemOrg: true, default);
        singleTenant.Should().ContainSingle(t => t.OrganizationId == TestDb.DefaultOrgId)
            .Which.Countries.Should().Be("dk");
    }

    [Fact]
    public async Task Regular_org_is_a_target_regardless_of_single_tenant_flag()
    {
        // Org 2 is a regular (non-system) org — always eligible when opted in.
        await EnableAutoImportAsync(TestDb.OtherOrgId, "w1,dk");

        await using var ctx = _db.NewContext();

        foreach (var includeSystemOrg in new[] { false, true })
        {
            var targets = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
                ctx, includeSystemOrg, default);
            targets.Should().ContainSingle(t => t.OrganizationId == TestDb.OtherOrgId)
                .Which.Countries.Should().Be("w1,dk");
        }
    }

    private async Task EnableAutoImportAsync(int organizationId, string country)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
            AutoImportReleasesEnabled = true,
            AutoImportCountry = country,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
