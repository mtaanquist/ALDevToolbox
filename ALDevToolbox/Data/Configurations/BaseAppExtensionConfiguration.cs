using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class BaseAppExtensionConfiguration : IEntityTypeConfiguration<BaseAppExtension>
{
    public void Configure(EntityTypeBuilder<BaseAppExtension> entity)
    {
        entity.ToTable("base_app_extensions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.VersionId).HasColumnName("version_id").IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Publisher).HasColumnName("publisher").IsRequired();
        entity.Property(e => e.AppVersion).HasColumnName("app_version").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Version)
            .WithMany()
            .HasForeignKey(e => e.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One extension per (version, app_id). Re-importing the same app
        // into the same version reuses the existing row rather than
        // creating a duplicate; AppId is the stable GUID from app.json.
        entity.HasIndex(e => new { e.VersionId, e.AppId }).IsUnique();
    }
}
