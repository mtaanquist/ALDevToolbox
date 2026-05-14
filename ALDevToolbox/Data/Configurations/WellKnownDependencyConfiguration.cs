using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class WellKnownDependencyConfiguration : IEntityTypeConfiguration<WellKnownDependency>
{

    public void Configure(EntityTypeBuilder<WellKnownDependency> entity)
    {
        entity.ToTable("well_known_dependencies");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.DepId).HasColumnName("dep_id").IsRequired();
        entity.Property(e => e.DepName).HasColumnName("dep_name").IsRequired();
        entity.Property(e => e.DepPublisher).HasColumnName("dep_publisher").IsRequired();
        entity.Property(e => e.DepVersionDefault).HasColumnName("dep_version_default").IsRequired();
        entity.Property(e => e.Category).HasColumnName("category");
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(e => new { e.OrganizationId, e.Ordering });
    }
}
