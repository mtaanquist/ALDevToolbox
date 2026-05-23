using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Behavioural tests for the organisation identity surface added with the
/// org-identity rework: renaming, email-domain claim CRUD, and the
/// signup-time domain routing.
/// </summary>
public sealed class OrganizationIdentityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Rename_rejects_names_shorter_than_two_characters()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        Func<Task> act = () => svc.RenameOrganizationAsync("A");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Rename_invalidates_the_cached_name_so_the_top_bar_picks_it_up()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        // Prime the cache with the seeded "Default" name.
        var before = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        before.Should().Be("Default");

        await svc.RenameOrganizationAsync("Renamed Co");

        // Next read should see the new name — without re-querying the DB
        // for "before" but after rename the cache must have been busted.
        var after = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        after.Should().Be("Renamed Co",
            "the top-bar lookup is cached per-org and rename must invalidate it");
    }

    [Fact]
    public async Task Rename_persists_trimmed_name_and_keeps_slug()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.RenameOrganizationAsync("  Acme Holdings  ");

        await using var read = _db.NewContext();
        var org = await read.Organizations.AsNoTracking().FirstAsync(o => o.Id == TestDb.DefaultOrgId);
        org.Name.Should().Be("Acme Holdings");
        org.Slug.Should().NotBeEmpty("renaming does not touch the slug");
    }

    [Fact]
    public async Task Add_normalises_case_and_strips_leading_at()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        await svc.AddEmailDomainAsync("@Acme.COM");

        await using var read = _db.NewContext();
        var rows = await read.OrganizationEmailDomains.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Domain.Should().Be("acme.com");
    }

    [Fact]
    public async Task Add_rejects_a_domain_already_claimed_by_another_org()
    {
        // Seed an email-domain claim against the Other org.
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = TestDb.OtherOrgId,
                Domain = "shared.example",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.AddEmailDomainAsync("shared.example");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Domain");
        ex.Which.Errors["Domain"].Should().Contain("another organisation");
    }

    [Fact]
    public async Task Add_rejects_garbage_domains_with_a_friendly_error()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.AddEmailDomainAsync("not a domain");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Domain");
    }

    [Fact]
    public async Task Remove_cannot_delete_another_orgs_domain()
    {
        int otherDomainId;
        await using (var seed = _db.NewContext())
        {
            var row = new OrganizationEmailDomain
            {
                OrganizationId = TestDb.OtherOrgId,
                Domain = "untouchable.example",
                CreatedAt = DateTime.UtcNow,
            };
            seed.OrganizationEmailDomains.Add(row);
            await seed.SaveChangesAsync();
            otherDomainId = row.Id;
        }

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        // Service is scoped to the Default org (TestDb.OrgContext). The query
        // filter prevents it from seeing the row, so the call is a silent no-op.
        await svc.RemoveEmailDomainAsync(otherDomainId);

        await using var read = _db.NewContext();
        var stillThere = await read.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AnyAsync(d => d.Id == otherDomainId);
        stillThere.Should().BeTrue("the query filter must scope the delete to the calling org");
    }

    [Fact]
    public async Task SetMcpEnabled_refuses_when_site_wide_disabled()
    {
        _db.McpAvailability.Set(false);

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.SetMcpEnabledAsync(true);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("McpEnabled");
    }

    [Fact]
    public async Task SetMcpEnabled_round_trips_when_site_wide_on()
    {
        _db.McpAvailability.Set(true);

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);
        await svc.SetMcpEnabledAsync(false);

        await using var read = _db.NewContext();
        var org = await read.Organizations.AsNoTracking().FirstAsync(o => o.Id == TestDb.DefaultOrgId);
        org.McpEnabled.Should().BeFalse();
    }
}
