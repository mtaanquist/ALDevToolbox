using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
        entity.Property(e => e.CustomerName).HasColumnName("customer_name").IsRequired();
        entity.Property(e => e.DownloadedByUserId).HasColumnName("downloaded_by_user_id");
        entity.Property(e => e.DownloadedAt).HasColumnName("downloaded_at").IsRequired();
        // Drives the admin "applied to customers" panel: newest-first per recipe.
        entity.HasIndex(e => new { e.OrganizationId, e.RecipeId, e.DownloadedAt });
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
        // SetNull so removing a user doesn't erase the customer-trace history.
        entity.HasOne(e => e.DownloadedByUser)
            .WithMany()
            .HasForeignKey(e => e.DownloadedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
