using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationAssetConfiguration : IEntityTypeConfiguration<OrganizationAsset>
{

    public void Configure(EntityTypeBuilder<OrganizationAsset> entity)
    {
        entity.ToTable("organization_assets");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
        entity.Property(e => e.ContentType).HasColumnName("content_type").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Kind }).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
