using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class BcArtifactVersionConfiguration : IEntityTypeConfiguration<BcArtifactVersion>
{
    public void Configure(EntityTypeBuilder<BcArtifactVersion> entity)
    {
        entity.ToTable("oe_artifact_versions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Country).HasColumnName("country").HasMaxLength(20).IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(40).IsRequired();
        entity.Property(e => e.MajorMinor).HasColumnName("major_minor").HasMaxLength(20).IsRequired();
        entity.Property(e => e.ApplicationUrl).HasColumnName("application_url").IsRequired();
        entity.Property(e => e.RefreshedAt).HasColumnName("refreshed_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Refresh upserts on (org, country, version) rather than duplicating rows.
        entity.HasIndex(e => new { e.OrganizationId, e.Country, e.Version })
            .IsUnique()
            .HasDatabaseName("ix_oe_artifact_versions_org_country_version");
    }
}
