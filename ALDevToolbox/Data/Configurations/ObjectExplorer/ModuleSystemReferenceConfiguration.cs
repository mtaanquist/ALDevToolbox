using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleSystemReferenceConfiguration : IEntityTypeConfiguration<ModuleSystemReference>
{
    public void Configure(EntityTypeBuilder<ModuleSystemReference> entity)
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

        // Primary "find system references" query: receiver triplet → matching rows,
        // resolved through the recursive release-chain CTE (FindSystemReferencesAsync).
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_system_references_target_id");

        // Name fallback when ObjectId is null (interfaces, some extensions).
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_system_references_target_name");

        // Module-scoped resolution: the C/AL import's id→name post-pass UPDATEs
        // every row in one module by (module_id, target_object_kind, target_object_id).
        entity.HasIndex(e => new { e.ModuleId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_system_references_module_target");

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
