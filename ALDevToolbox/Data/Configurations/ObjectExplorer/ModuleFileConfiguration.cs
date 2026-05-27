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

        // The source text lives in the shared, deduplicated oe_file_contents
        // store, keyed by content_hash. Restrict (not cascade): deleting this
        // file row must never drop a content blob another file/org still
        // references — orphans are reclaimed on hard-purge (see
        // ReleaseManagementService.HardDeleteAsync).
        entity.HasOne(e => e.FileContent)
            .WithMany()
            .HasForeignKey(e => e.ContentHash)
            .HasPrincipalKey(c => c.ContentHash)
            .OnDelete(DeleteBehavior.Restrict);

        // Unique within a module so re-ingest can upsert deterministically by path.
        entity.HasIndex(e => new { e.ModuleId, e.Path })
            .IsUnique()
            .HasDatabaseName("ix_oe_module_files_module_path");

        // Backs the content_hash FK and the orphan-content anti-join sweep.
        entity.HasIndex(e => e.ContentHash)
            .HasDatabaseName("ix_oe_module_files_content_hash");
    }
}
