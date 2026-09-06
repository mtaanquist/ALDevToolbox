using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class TranslationMemorySourceConfiguration : IEntityTypeConfiguration<TranslationMemorySource>
{
    public void Configure(EntityTypeBuilder<TranslationMemorySource> entity)
    {
        entity.ToTable("translation_memory_sources");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300).IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").HasMaxLength(1000).IsRequired();
        entity.Property(e => e.BlobSha).HasColumnName("blob_sha").HasMaxLength(100).IsRequired();
        entity.Property(e => e.LastIngestedAt).HasColumnName("last_ingested_at").IsRequired();
        entity.Property(e => e.UnitCount).HasColumnName("unit_count").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per file. The ingest looks a file up by exactly this key to
        // decide whether its sha has moved, and the uniqueness is what stops two
        // sweeps racing into a duplicate.
        entity.HasIndex(e => new { e.OrganizationId, e.Repository, e.Path })
            .IsUnique()
            .HasDatabaseName("ux_translation_memory_sources_file");
    }
}
