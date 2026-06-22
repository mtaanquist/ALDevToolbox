using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// First-run seeding for single-tenant deployments: names the system org and
/// claims its email domains from env vars, flipping auto-join on so verified
/// domain users join straight through. See
/// <see cref="SingleTenantSeeder"/>.
/// </summary>
public sealed class SingleTenantSeederTests : IDisposable
{
    private readonly TestDb _db = new();
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Seeds_org_identity_and_email_domains_with_auto_join()
    {
        await using (var ctx = _db.NewContext())
        {
            await SingleTenantSeeder.SeedAsync(
                ctx, orgName: "Acme", orgSlug: "acme",
                emailDomainsCsv: "Acme.com, @sub.acme.com",
                Now, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = _db.NewContext();
        var org = await read.Organizations.IgnoreQueryFilters().FirstAsync(o => o.IsSystem);
        org.Name.Should().Be("Acme");
        org.Slug.Should().Be("acme");

        var domains = await read.OrganizationEmailDomains.IgnoreQueryFilters()
            .Where(d => d.OrganizationId == org.Id)
            .Select(d => d.Domain)
            .ToListAsync();
        domains.Should().BeEquivalentTo(new[] { "acme.com", "sub.acme.com" },
            "domains are lowercased and the leading @ stripped");

        var settings = await read.OrganizationSettings.IgnoreQueryFilters()
            .FirstAsync(s => s.OrganizationId == org.Id);
        settings.AutoJoinVerifiedDomainUsers.Should().BeTrue();
    }

    [Fact]
    public async Task Skips_invalid_domains_and_does_not_enable_auto_join_without_a_valid_one()
    {
        await using (var ctx = _db.NewContext())
        {
            await SingleTenantSeeder.SeedAsync(
                ctx, orgName: null, orgSlug: null,
                emailDomainsCsv: "not a domain",
                Now, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = _db.NewContext();
        var org = await read.Organizations.IgnoreQueryFilters().FirstAsync(o => o.IsSystem);
        (await read.OrganizationEmailDomains.IgnoreQueryFilters()
            .AnyAsync(d => d.OrganizationId == org.Id)).Should().BeFalse();
        (await read.OrganizationSettings.IgnoreQueryFilters()
            .AnyAsync(s => s.OrganizationId == org.Id && s.AutoJoinVerifiedDomainUsers)).Should().BeFalse();
    }

    [Fact]
    public async Task Skips_a_domain_already_claimed_by_another_org()
    {
        // The seeded "Other" org claims acme.com first.
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = TestDb.OtherOrgId, Domain = "acme.com", CreatedAt = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            await SingleTenantSeeder.SeedAsync(
                ctx, orgName: "Acme", orgSlug: null,
                emailDomainsCsv: "acme.com",
                Now, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = _db.NewContext();
        var systemOrg = await read.Organizations.IgnoreQueryFilters().FirstAsync(o => o.IsSystem);
        // The name still applied, but the taken domain was not duplicated onto the system org.
        systemOrg.Name.Should().Be("Acme");
        (await read.OrganizationEmailDomains.IgnoreQueryFilters()
            .CountAsync(d => d.Domain == "acme.com")).Should().Be(1);
        (await read.OrganizationEmailDomains.IgnoreQueryFilters()
            .AnyAsync(d => d.OrganizationId == systemOrg.Id)).Should().BeFalse();
    }
}
