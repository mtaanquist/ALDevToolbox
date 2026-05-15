using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleReferenceConfiguration : IEntityTypeConfiguration<ModuleReference>
{
    public void Configure(EntityTypeBuilder<ModuleReference> entity)
    {
        entity.ToTable("oe_module_references");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.SourceObjectId).HasColumnName("source_object_id").IsRequired();
        entity.Property(e => e.TargetAppId).HasColumnName("target_app_id").IsRequired();
        entity.Property(e => e.TargetObjectKind).HasColumnName("target_object_kind").IsRequired();
        entity.Property(e => e.TargetObjectId).HasColumnName("target_object_id");
        entity.Property(e => e.TargetObjectName).HasColumnName("target_object_name").IsRequired();
        entity.Property(e => e.ReferenceKind).HasColumnName("reference_kind").IsRequired();
        entity.Property(e => e.LineNumber).HasColumnName("line_number");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.References)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.SourceObject)
            .WithMany()
            .HasForeignKey(e => e.SourceObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary find-references query: target triplet → matching rows. Used by the
        // recursive-CTE join in ObjectExplorerService.FindReferencesAsync (PR 4).
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_references_target_id");

        // Fallback when ObjectId is null on the symbol-package side (interfaces, some
        // extensions) — match by name instead.
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_references_target_name");

        // Reverse direction: enumerate references originating from a specific object,
        // used when displaying outbound references from the inspector panel.
        entity.HasIndex(e => e.SourceObjectId)
            .HasDatabaseName("ix_oe_module_references_source_object");
    }
}
