using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RecipeDownloadConfiguration : IEntityTypeConfiguration<RecipeDownload>
{
    public void Configure(EntityTypeBuilder<RecipeDownload> entity)
    {
        entity.ToTable("recipe_downloads");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.RecipeId).HasColumnName("recipe_id").IsRequired();
        // Nullable: null is "not recorded", which beats the junk a required
        // field collects from anyone downloading for a demo. See issue #539.
        entity.Property(e => e.CustomerName).HasColumnName("customer_name");
        // Stored as text rather than an ordinal so a psql session reading the
        // table doesn't have to know the enum's declaration order.
        entity.Property(e => e.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        // The repository a Repository-sourced use landed in, as owner/name.
        // Null for downloads and copies. 300 characters is well past GitHub's
        // own limit for owner + name, so a stored value is never truncated.
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300);
        // Resolved from the customer name at record time (exact, case-insensitive,
        // active projects only). See issue #541.
        entity.Property(e => e.ProjectId).HasColumnName("project_id");
        entity.Property(e => e.DownloadedByUserId).HasColumnName("downloaded_by_user_id");
        entity.Property(e => e.DownloadedAt).HasColumnName("downloaded_at").IsRequired();
        // Drives the admin "applied to customers" panel: newest-first per recipe.
        entity.HasIndex(e => new { e.OrganizationId, e.RecipeId, e.DownloadedAt });
        // Non-unique: answers "which recipes has this project taken" as a join
        // rather than a scan. See issue #541.
        entity.HasIndex(e => e.ProjectId);
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        // Cascade with the recipe — once a recipe is hard-deleted its download
        // history goes with it.
        entity.HasOne(e => e.Recipe)
            .WithMany()
            .HasForeignKey(e => e.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
        // SetNull so hard-deleting a project doesn't erase the download history -
        // the free-text customer name survives as the label either way.
        entity.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);
        // SetNull so removing a user doesn't erase the customer-trace history.
        entity.HasOne(e => e.DownloadedByUser)
            .WithMany()
            .HasForeignKey(e => e.DownloadedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
