using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="SiteAdminService"/>: cross-organisation
/// search bypasses the per-org query filter, promote / demote toggles the
/// IsSiteAdmin flag (and refuses to demote the last SiteAdmin), and audit
/// search returns rows from multiple organisations in one call.
/// </summary>
public sealed class SiteAdminServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Search_returns_users_from_multiple_organisations_for_a_site_admin()
    {
        await SeedUsers();

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        // "example.com" matches every seeded email — both orgs' users appear.
        var rows = await svc.SearchUsersAsync("example.com");

        rows.Select(r => r.OrganizationId).Distinct().Should().HaveCountGreaterOrEqualTo(2,
            "the SiteAdmin search bypasses the per-org query filter");
        rows.Should().Contain(r => r.Email == "alice@example.com");
        rows.Should().Contain(r => r.Email == "bob@example.com");
    }

    [Fact]
    public async Task Get_memberships_lists_every_org_for_an_email()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                NewUser("dual@example.com", TestDb.DefaultOrgId, role: UserRole.User),
                NewUser("dual@example.com", TestDb.OtherOrgId, role: UserRole.Admin));
            await seed.SaveChangesAsync();
        }

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        var memberships = await svc.GetMembershipsAsync("dual@example.com");
        memberships.Select(m => m.OrganizationId).Should().Contain(new[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
    }

    [Fact]
    public async Task Promote_sets_is_site_admin_true()
    {
        var userId = await SeedSingleUser();

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        await svc.PromoteAsync(userId);

        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.IsSiteAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task Demote_refuses_to_remove_the_last_site_admin()
    {
        var userId = await SeedSingleUser(siteAdmin: true);

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        Func<Task> demote = () => svc.DemoteAsync(userId);
        var ex = await demote.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("LastSiteAdmin");
    }

    [Fact]
    public async Task Demote_succeeds_when_another_site_admin_remains()
    {
        var firstId = await SeedSingleUser(siteAdmin: true);
        var secondId = await SeedSingleUser(email: "second@example.com", siteAdmin: true);

        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        await svc.DemoteAsync(firstId);

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == firstId)).IsSiteAdmin.Should().BeFalse();
        (await read.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == secondId)).IsSiteAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task Promote_without_site_admin_context_throws()
    {
        var userId = await SeedSingleUser();
        var ctx = _db.NewContext();
        var svc = new SiteAdminService(ctx, _db.OrgContext, NullLogger<SiteAdminService>.Instance);
        Func<Task> act = () => svc.PromoteAsync(userId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Audit_search_spans_organisations()
    {
        // The audit interceptor stamps OrganizationId from the entity itself
        // (RuntimeTemplate.OrganizationId). Adding rows in two different orgs
        // produces audit rows in both — and SearchAuditAsync should return
        // both because it bypasses the per-org query filter.
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            ctx.RuntimeTemplates.Add(Builders.TemplateBuilder.Default("rt-default", organizationId: TestDb.DefaultOrgId));
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            ctx.RuntimeTemplates.Add(Builders.TemplateBuilder.Default("rt-other", organizationId: TestDb.OtherOrgId));
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        var ctxSearch = _db.NewContext();
        var svc = new SiteAdminService(ctxSearch, NewSiteAdminContext(), NullLogger<SiteAdminService>.Instance);
        var rows = await svc.SearchAuditAsync(
            entityType: AuditEntityType.RuntimeTemplate,
            organizationId: null,
            actorContains: null,
            fromUtc: null,
            toUtc: null);

        rows.Select(r => r.OrganizationId).Where(o => o is not null).Distinct()
            .Should().Contain(new int?[] { TestDb.DefaultOrgId, TestDb.OtherOrgId });
    }

    private async Task SeedUsers()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser("alice@example.com", TestDb.DefaultOrgId),
            NewUser("bob@example.com", TestDb.OtherOrgId));
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedSingleUser(string email = "promote@example.com", bool siteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        var user = NewUser(email, TestDb.DefaultOrgId);
        user.IsSiteAdmin = siteAdmin;
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private static User NewUser(string email, int orgId, UserRole role = UserRole.User) => new()
    {
        OrganizationId = orgId,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private static AmbientOrganizationContext NewSiteAdminContext() => new()
    {
        CurrentOrganizationId = TestDb.DefaultOrgId,
        CurrentUserId = 1,
        IsSiteAdmin = true,
    };
}
