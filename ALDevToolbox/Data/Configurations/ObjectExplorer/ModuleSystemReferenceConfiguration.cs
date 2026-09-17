using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ALDevToolbox.Services.ObjectExplorer.Explore;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleSystemReferenceConfiguration : IEntityTypeConfiguration<OeModuleSystemReference>
{
    public void Configure(EntityTypeBuilder<OeModuleSystemReference> entity)
    {
        entity.ToTable("oe_module_system_references");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.SourceObjectId).HasColumnName("source_object_id").IsRequired();
        entity.Property(e => e.TargetAppId).HasColumnName("target_app_id").IsRequired();
        entity.Property(e => e.TargetObjectKind).HasColumnName("target_object_kind").IsRequired();
        entity.Property(e => e.TargetObjectId).HasColumnName("target_object_id");
        entity.Property(e => e.TargetObjectName).HasColumnName("target_object_name").IsRequired();
        entity.Property(e => e.SystemMethodName).HasColumnName("system_method_name").IsRequired();
        entity.Property(e => e.ReferenceKind).HasColumnName("reference_kind").IsRequired();
        entity.Property(e => e.LineNumber).HasColumnName("line_number");
        entity.Property(e => e.ColumnNumber).HasColumnName("column_number");
        entity.Property(e => e.SourceSymbolId).HasColumnName("source_symbol_id");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany()
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.SourceObject)
            .WithMany()
            .HasForeignKey(e => e.SourceObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.SourceSymbol)
            .WithMany()
            .HasForeignKey(e => e.SourceSymbolId)
            .OnDelete(DeleteBehavior.SetNull);

        // The app-keyed (target_app_id, target_object_kind, target_object_id/name)
        // pair that used to sit here is gone (#723). FindSystemReferencesAsync is
        // their only reader and both its branches lead with
        // module_id = ANY(<winning>), so the module-scoped pair below always won
        // the plan; production reported a lifetime zero scans on both across
        // 457 MB. Re-adding them means re-reading ReferenceQueryService first.

        // Module-scoped resolution: the C/AL import's id→name post-pass UPDATEs
        // every row in one module by (module_id, target_object_kind, target_object_id).
        // Also the id-branch of FindSystemReferencesAsync, which scopes the scan
        // to the Release's winning modules (see below).
        entity.HasIndex(e => new { e.ModuleId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_system_references_module_target");

        // Name-branch twin of the module-scoped index above — the
        // ix_oe_module_references_module_target_name analogue for system
        // references. FindSystemReferencesAsync matches a receiver across a
        // Release's visible module chain (module_id = ANY(<winning>)); without
        // this the name-branch has nothing module-scoped to seek on, and since
        // #723 dropped the app-keyed pair there is no app-keyed fallback either.
        // Partial on the null-id rows so it stays tiny, which is why it reads as
        // 8 kB and zero scans while no such rows exist. See ReferenceQueryService.
        entity.HasIndex(e => new { e.ModuleId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_system_references_module_target_name")
            .HasFilter("\"target_object_id\" IS NULL");

        // Outbound: system calls originating from a given object.
        entity.HasIndex(e => e.SourceObjectId)
            .HasDatabaseName("ix_oe_module_system_references_source_object");

        // Forward-edge "what system methods does this procedure call?", parity
        // with ix_oe_module_references_source_symbol — partial-filtered to the
        // minority of rows emitted from inside a procedure body. Also backs the
        // nullable source_symbol_id FK. See issue #391.
        entity.HasIndex(e => e.SourceSymbolId)
            .HasDatabaseName("ix_oe_module_system_references_source_symbol")
            .HasFilter("\"source_symbol_id\" IS NOT NULL");
    }
}
