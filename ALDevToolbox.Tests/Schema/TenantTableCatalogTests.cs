using ALDevToolbox.Data;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// Pins <see cref="TenantTableCatalog"/> to the EF model (#665).
///
/// <para>
/// The catalogue is hand-maintained and drives two things that fail silently
/// when it is wrong: per-org disk usage (a missing table is simply unbilled)
/// and the per-tenant backup, whose restore deletes every catalogued table for
/// the org and re-inserts from the snapshot. A table that is a cascade child
/// of a catalogued parent but absent from the catalogue is therefore deleted
/// and never restored — silent data loss, which is exactly what happened to
/// five tables before this test existed.
/// </para>
///
/// <para>
/// Pure model tests: they read <c>AppDbContext.Model</c> off a connectionless
/// options object, so no Postgres is needed.
/// </para>
/// </summary>
public sealed class TenantTableCatalogTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);
        return ctx.Model;
    }

    /// <summary>Distinct table names of entity types carrying an <c>organization_id</c> column.</summary>
    private static HashSet<string> TenantedTables(IModel model) =>
        model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Where(e => e.GetProperties().Any(p => p.GetColumnName() == "organization_id"))
            .Select(e => e.GetTableName()!)
            .ToHashSet();

    [Fact]
    public void Every_tenanted_table_is_catalogued_exactly_once()
    {
        var model = Model();

        var content = TenantTableCatalog.ContentTables.ToHashSet();
        var authAudit = TenantTableCatalog.AuthAndAuditTables.ToHashSet();
        var excluded = TenantTableCatalog.DeliberatelyExcluded;

        // No table may appear in two lists — the meaning of each is exclusive.
        content.Intersect(authAudit).Should().BeEmpty("a table is either restorable content or auth/audit state");
        content.Intersect(excluded.Keys).Should().BeEmpty();
        authAudit.Intersect(excluded.Keys).Should().BeEmpty();

        // Duplicates inside ContentTables would delete/insert the same table twice.
        TenantTableCatalog.ContentTables.Should().OnlyHaveUniqueItems();
        TenantTableCatalog.AuthAndAuditTables.Should().OnlyHaveUniqueItems();

        var uncatalogued = TenantedTables(model)
            .Where(t => !content.Contains(t) && !authAudit.Contains(t) && !excluded.ContainsKey(t))
            .OrderBy(t => t)
            .ToList();
        uncatalogued.Should().BeEmpty(
            "every table with an organization_id must be listed in TenantTableCatalog.ContentTables "
            + "(restored by a per-tenant restore), AuthAndAuditTables (counted for usage, never restored) "
            + "or DeliberatelyExcluded (with a reason)");

        // Every exclusion carries a real reason, not an empty string.
        foreach (var (table, reason) in excluded)
        {
            reason.Should().NotBeNullOrWhiteSpace($"{table} must say why it is excluded");
        }

        // And nothing is catalogued that the model doesn't have — except the
        // handful of auth tables that reach the org through users.
        var known = TenantedTables(model);
        foreach (var table in content.Concat(excluded.Keys)
                     .Concat(authAudit.Where(t => !TenantTableCatalog.TablesLinkedThroughUser.ContainsKey(t))))
        {
            known.Should().Contain(table, $"{table} is catalogued but no entity maps to it with an organization_id");
        }
    }

    [Fact]
    public void Everything_cascade_deleted_by_a_restore_is_restored_again()
    {
        var model = Model();
        var content = TenantTableCatalog.ContentTables.ToHashSet();

        // A restore issues DELETE ... WHERE organization_id = @org against every
        // ContentTables entry. Postgres cascades that to the FK children, so any
        // child of a catalogued parent must itself be catalogued (or be an
        // explicit exclusion whose reason acknowledges the cascade).
        var offenders = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || content.Contains(table)) continue;

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.DeleteBehavior != DeleteBehavior.Cascade) continue;
                var parent = fk.PrincipalEntityType.GetTableName();
                if (parent is null || parent == table || !content.Contains(parent)) continue;

                if (TenantTableCatalog.DeliberatelyExcluded.TryGetValue(table, out var reason)
                    && reason.Contains("cascade", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                offenders.Add($"{table} (cascade child of {parent})");
            }
        }

        offenders.Distinct().OrderBy(x => x).Should().BeEmpty(
            "a per-tenant restore deletes these rows via the cascade and never puts them back");
    }

    [Fact]
    public void ContentTables_is_in_valid_foreign_key_order()
    {
        var model = Model();
        var position = TenantTableCatalog.ContentTables
            .Select((t, i) => (t, i))
            .ToDictionary(x => x.t, x => x.i);

        var violations = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || !position.TryGetValue(table, out var childIndex)) continue;

            foreach (var fk in entity.GetForeignKeys())
            {
                var parent = fk.PrincipalEntityType.GetTableName();
                // Self-references order rows inside one table, not the table list.
                if (parent is null || parent == table) continue;
                if (!position.TryGetValue(parent, out var parentIndex)) continue;
                if (parentIndex > childIndex)
                {
                    violations.Add($"{table} references {parent}, which is inserted later");
                }
            }
        }

        violations.Distinct().OrderBy(x => x).Should().BeEmpty(
            "the restore inserts ContentTables in order, so every referenced table must come first");
    }
}
