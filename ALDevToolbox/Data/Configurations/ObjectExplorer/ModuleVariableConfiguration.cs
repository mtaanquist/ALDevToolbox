using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleVariableConfiguration : IEntityTypeConfiguration<ModuleVariable>
{
    public void Configure(EntityTypeBuilder<ModuleVariable> entity)
    {
        entity.ToTable("oe_module_variables");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.ObjectId).HasColumnName("object_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.TypeKeyword).HasColumnName("type_keyword");
        entity.Property(e => e.TypeName).HasColumnName("type_name").IsRequired();
        entity.Property(e => e.TargetAppId).HasColumnName("target_app_id");
        entity.Property(e => e.TargetObjectKind).HasColumnName("target_object_kind");
        entity.Property(e => e.TargetObjectId).HasColumnName("target_object_id");
        entity.Property(e => e.TargetObjectName).HasColumnName("target_object_name");
        entity.Property(e => e.LineNumber).HasColumnName("line_number").IsRequired();
        entity.Property(e => e.ColumnStart).HasColumnName("column_start").IsRequired();
        entity.Property(e => e.ColumnEnd).HasColumnName("column_end").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.Variables)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Object)
            .WithMany(o => o.Variables)
            .HasForeignKey(e => e.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Receiver-aware resolution lookup: "is there a variable on this object whose type is
        // the declaring module's AppId+kind+name?" Answers the band-aid classifier's question
        // in one query rather than regex-extracting from source.
        entity.HasIndex(e => new { e.ObjectId, e.Name })
            .HasDatabaseName("ix_oe_module_variables_object_name");

        // Cross-module "who else has a variable typed to this object?" lookup.
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_variables_target_id");

        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_variables_target_name");
    }
}
