using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Verifies the EF query filter scopes reads to the acting org. Cross-org
/// content from another tenant is invisible until tests explicitly switch
/// the ambient context — which is exactly the boundary the milestone calls
/// for.
/// </summary>
public sealed class CrossOrgIsolationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Templates_in_other_org_are_invisible()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-other", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        // Default org sees only its own template.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.Select(t => t.Key).ToListAsync())
                .Should().BeEquivalentTo(new[] { "runtime-default" });
        }

        // Other org sees only its own template.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.Select(t => t.Key).ToListAsync())
                .Should().BeEquivalentTo(new[] { "runtime-other" });
        }

        // No org context: no rows visible (filter sentinel is 0).
        _db.OrgContext.CurrentOrganizationId = null;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.RuntimeTemplates.AnyAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Modules_and_catalogue_scope_per_org()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("mod-default", organizationId: TestDb.DefaultOrgId));
            seed.Modules.Add(ModuleBuilder.Default("mod-other", organizationId: TestDb.OtherOrgId));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000001", "Default Dep", organizationId: TestDb.DefaultOrgId));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000002", "Other Dep", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Modules.Select(m => m.Key).ToListAsync()).Should().Equal("mod-default");
            (await ctx.WellKnownDependencies.Select(w => w.DepName).ToListAsync()).Should().Equal("Default Dep");
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Modules.Select(m => m.Key).ToListAsync()).Should().Equal("mod-other");
            (await ctx.WellKnownDependencies.Select(w => w.DepName).ToListAsync()).Should().Equal("Other Dep");
        }
    }

    [Fact]
    public async Task IgnoreQueryFilters_lets_pre_login_paths_read_across_orgs()
    {
        // The /login path needs to look up users without knowing which org
        // they're in yet. IgnoreQueryFilters() is the contract for that.
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.OtherOrgId,
                Email = "alice@example.com",
                PasswordHash = "x",
                DisplayName = "Alice",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Users.AnyAsync(u => u.Email == "alice@example.com"))
                .Should().BeFalse("query filter scopes to the Default org");
            (await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "alice@example.com"))
                .Should().BeTrue();
        }
    }
}
