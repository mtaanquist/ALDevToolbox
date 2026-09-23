using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ReleaseDependencyConfiguration : IEntityTypeConfiguration<OeReleaseDependency>
{
    public void Configure(EntityTypeBuilder<OeReleaseDependency> entity)
    {
        entity.ToTable("oe_release_dependencies");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ReleaseId).HasColumnName("release_id").IsRequired();
        entity.Property(e => e.DependencyReleaseId).HasColumnName("dependency_release_id").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // One link per pair. Leads with release_id, so it is also the index the
        // chain walk's dependency seed reads.
        entity.HasIndex(e => new { e.ReleaseId, e.DependencyReleaseId })
            .IsUnique()
            .HasDatabaseName("ix_oe_release_dependencies_release_dependency");
        // Covers the cascade when a vendor Release is deleted.
        entity.HasIndex(e => e.DependencyReleaseId)
            .HasDatabaseName("ix_oe_release_dependencies_dependency_release");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // A link means nothing once either end is gone, so deleting either
        // Release takes its links with it.
        entity.HasOne(e => e.Release)
            .WithMany()
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.DependencyRelease)
            .WithMany()
            .HasForeignKey(e => e.DependencyReleaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
