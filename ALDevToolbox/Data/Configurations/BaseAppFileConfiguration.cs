using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class BaseAppFileConfiguration : IEntityTypeConfiguration<BaseAppFile>
{
    public void Configure(EntityTypeBuilder<BaseAppFile> entity)
    {
        entity.ToTable("base_app_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.VersionId).HasColumnName("version_id").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Module).HasColumnName("module");
        entity.Property(e => e.ObjectType).HasColumnName("object_type").IsRequired();
        entity.Property(e => e.ObjectId).HasColumnName("object_id");
        entity.Property(e => e.ObjectName).HasColumnName("object_name").IsRequired();
        entity.Property(e => e.Namespace).HasColumnName("namespace");
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.ContentHash).HasColumnName("content_hash");
        entity.Property(e => e.LineCount).HasColumnName("line_count").IsRequired();
        entity.Property(e => e.ExtensionId).HasColumnName("extension_id");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Files keep their row when the extension is deleted (SetNull)
        // so we don't lose source from the symbol index if the metadata
        // gets cleaned up. The extension itself is cascade-deleted with
        // the version, so this only matters in test/admin scenarios.
        entity.HasOne(e => e.Extension)
            .WithMany(x => x.Files)
            .HasForeignKey(e => e.ExtensionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Primary list ordering for the browser table.
        entity.HasIndex(e => new { e.VersionId, e.ObjectType, e.ObjectName });

        // Counterpart lookup when diffing across versions.
        entity.HasIndex(e => new { e.VersionId, e.ObjectType, e.ObjectId });

        // Extension filter lookup.
        entity.HasIndex(e => new { e.VersionId, e.ExtensionId });

        // Trigram indexes on lower(content) and lower(object_name) are added
        // via raw SQL in the migration — EF doesn't model GIN/gin_trgm_ops.
    }
}
