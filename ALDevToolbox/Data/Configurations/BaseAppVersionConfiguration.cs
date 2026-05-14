using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class BaseAppVersionConfiguration : IEntityTypeConfiguration<BaseAppVersion>
{
    public void Configure(EntityTypeBuilder<BaseAppVersion> entity)
    {
        entity.ToTable("base_app_versions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Major).HasColumnName("major").IsRequired();
        entity.Property(e => e.CumulativeUpdate).HasColumnName("cumulative_update").IsRequired();
        entity.Property(e => e.ApplicationVersionId).HasColumnName("application_version_id");
        entity.Property(e => e.Notes).HasColumnName("notes");
        entity.Property(e => e.FileCount).HasColumnName("file_count").IsRequired();
        entity.Property(e => e.SymbolsIndexedAt).HasColumnName("symbols_indexed_at");
        entity.Property(e => e.UploadedAt).HasColumnName("uploaded_at").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.ApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.Files)
            .WithOne(f => f.Version!)
            .HasForeignKey(f => f.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One active version per (org, major, cumulative_update). Filter
        // on DeletedAt IS NULL so a soft-deleted row doesn't block re-importing
        // the same CU.
        entity.HasIndex(e => new { e.OrganizationId, e.Major, e.CumulativeUpdate })
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
    }
}
