using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectDeliveryResultConfiguration : IEntityTypeConfiguration<ProjectDeliveryResult>
{
    public void Configure(EntityTypeBuilder<ProjectDeliveryResult> entity)
    {
        entity.ToTable("oe_project_delivery_results");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectDeliveryId).HasColumnName("project_delivery_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(100);
        entity.Property(e => e.AppName).HasColumnName("app_name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.AppVersion).HasColumnName("app_version").HasMaxLength(50).IsRequired();
        entity.Property(e => e.ExtensionUploadId).HasColumnName("extension_upload_id").HasMaxLength(100);
        entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        entity.Property(e => e.Message).HasColumnName("message");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Results are owned by the delivery — cascade.
        entity.HasOne(e => e.ProjectDelivery)
            .WithMany(d => d.Results)
            .HasForeignKey(e => e.ProjectDeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.ProjectDeliveryId, e.Ordering })
            .HasDatabaseName("ix_oe_project_delivery_results_delivery_order");
    }
}
