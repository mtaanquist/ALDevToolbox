using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleFileConfiguration : IEntityTypeConfiguration<ModuleFile>
{
    public void Configure(EntityTypeBuilder<ModuleFile> entity)
    {
        entity.ToTable("oe_module_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.ContentHash).HasColumnName("content_hash").IsRequired();
        entity.Property(e => e.LineCount).HasColumnName("line_count").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.Files)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique within a module so re-ingest can upsert deterministically by path.
        entity.HasIndex(e => new { e.ModuleId, e.Path })
            .IsUnique()
            .HasDatabaseName("ix_oe_module_files_module_path");

        // Trigram GIN on lower(content) and lower(path) for the file-browser search later —
        // added via raw SQL in the migration since EF doesn't model gin_trgm_ops.
    }
}
