using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace ALDevToolbox.Data;

/// <summary>
/// EF's <see cref="ForeignKeyIndexConvention"/> gives every foreign key a
/// covering index, and re-creates it if a configuration removes it. On the
/// Object Explorer fact tables the <c>organization_id</c> key never earns one:
/// the tenant filter is always ANDed onto a <c>module_id</c> /
/// <c>target_app_id</c> / <c>source_object_id</c> predicate with its own
/// compound index, the hand-written reference SQL bypasses EF, and the usage
/// sweep estimates these tables from module share instead of scanning them. A
/// production install reported <c>idx_scan = 0</c> on all five while they cost
/// about 2.8 GB and one extra btree write per ingested row (issue #691).
///
/// This subclass keeps the convention for everything else and only declines to
/// create the single-column <c>organization_id</c> index on those five tables.
/// The foreign key constraint itself is unchanged, so tenant isolation (the EF
/// query filter plus the FK) does not depend on this index in any mode.
///
/// <para>
/// Known trade-off for multi-tenant installs: <c>PerTenantBackupService</c>
/// exports and restores these tables with a plain
/// <c>WHERE organization_id = @org</c>, and deleting an organisation cascades
/// through the same column. Without the index those become sequential scans
/// of the whole table (minutes on a multi-GB references table), still correct
/// but slow. Per-tenant snapshots are disabled in single-tenant mode, where
/// every current deployment runs, so the write saving on release import wins.
/// If a large multi-org deployment ever needs those paths fast, add a
/// composite <c>(organization_id, module_id)</c> index on the five tables
/// here rather than reinstating the bare one; it serves the snapshot queries
/// while staying useful to nothing else.
/// </para>
/// </summary>
internal sealed class OeFactTableForeignKeyIndexConvention : ForeignKeyIndexConvention
{
    private static readonly HashSet<Type> FactTables =
    [
        typeof(ModuleObject),
        typeof(ModuleReference),
        typeof(ModuleSymbol),
        typeof(ModuleVariable),
        typeof(ModuleSystemReference),
    ];

    public OeFactTableForeignKeyIndexConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    protected override IConventionIndex? CreateIndex(
        IReadOnlyList<IConventionProperty> properties,
        bool unique,
        IConventionEntityTypeBuilder entityTypeBuilder)
    {
        if (properties.Count == 1
            && properties[0].Name == "OrganizationId"
            && FactTables.Contains(entityTypeBuilder.Metadata.ClrType))
        {
            return null;
        }

        return base.CreateIndex(properties, unique, entityTypeBuilder);
    }
}
