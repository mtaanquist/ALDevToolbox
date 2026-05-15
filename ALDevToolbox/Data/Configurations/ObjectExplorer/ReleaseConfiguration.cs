using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> entity)
    {
        entity.ToTable("oe_releases");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Label).HasColumnName("label").IsRequired();
        entity.Property(e => e.BcVersion).HasColumnName("bc_version");
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.ParentReleaseId).HasColumnName("parent_release_id");
        entity.Property(e => e.ApplicationVersionId).HasColumnName("application_version_id");
        entity.Property(e => e.Status).HasColumnName("status").IsRequired();
        entity.Property(e => e.StatusMessage).HasColumnName("status_message");
        entity.Property(e => e.ImportedAt).HasColumnName("imported_at").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Parent Release deletion is refused while children still point at it — see
        // .design/object-explorer.md "Storage growth across many Releases" open question.
        entity.HasOne(e => e.ParentRelease)
            .WithMany()
            .HasForeignKey(e => e.ParentReleaseId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.ApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.ApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Per-org label uniqueness on active rows so the picker doesn't show duplicates.
        entity.HasIndex(e => new { e.OrganizationId, e.Label })
            .IsUnique()
            .HasFilter("\"deleted_at\" IS NULL")
            .HasDatabaseName("ix_oe_releases_org_label_active");

        // Chain walk: ancestors and descendants by parent pointer.
        entity.HasIndex(e => e.ParentReleaseId)
            .HasDatabaseName("ix_oe_releases_parent");
    }
}
