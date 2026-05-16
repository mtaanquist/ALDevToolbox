using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class PerTenantBackupConfiguration : IEntityTypeConfiguration<PerTenantBackup>
{
    public void Configure(EntityTypeBuilder<PerTenantBackup> entity)
    {
        entity.ToTable("per_tenant_backups");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
        entity.Property(e => e.SchemaVersion).HasColumnName("schema_version").IsRequired();
        entity.Property(e => e.IsPinned).HasColumnName("is_pinned").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt })
            .HasDatabaseName("ix_per_tenant_backups_org_created");
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        // Cross-org table accessed only by SiteAdmin paths; no query filter.
    }
}
