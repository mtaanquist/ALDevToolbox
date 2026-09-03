using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// The other hand-maintained tenancy list (#665): the ~60 explicit
/// <c>ScopeToOrganization&lt;T&gt;</c> calls in <c>AppDbContext</c>. The EF query
/// filter is the only thing stopping one org's request from reading another
/// org's rows, and forgetting a call is invisible at compile time. This test
/// makes the omission a red build.
/// </summary>
public sealed class TenantQueryFilterTests
{
    /// <summary>
    /// Tenanted tables that deliberately carry no query filter, with the reason.
    /// Nothing else may be added here without a maintainer decision.
    /// </summary>
    private static readonly Dictionary<string, string> Unfiltered = new()
    {
        ["organization_usage_snapshots"] = "Written and read off-request by the usage scheduler and the SiteAdmin storage page via raw SQL, both cross-org by design.",
        ["per_tenant_backups"] = "SiteAdmin-only surface; every read already calls IgnoreQueryFilters() to list snapshots across orgs.",
    };

    [Fact]
    public void Every_entity_with_an_organization_id_is_scoped_by_a_query_filter()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        var unscoped = ctx.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Where(e => e.GetProperties().Any(p => p.GetColumnName() == "organization_id"))
            .Where(e => (e.GetDeclaredQueryFilters()?.Count ?? 0) == 0)
            .Select(e => e.GetTableName()!)
            .Distinct()
            .Where(t => !Unfiltered.ContainsKey(t))
            .OrderBy(t => t)
            .ToList();

        unscoped.Should().BeEmpty(
            "every tenanted entity needs a ScopeToOrganization<T>() call in AppDbContext.OnModelCreating");
    }
}
