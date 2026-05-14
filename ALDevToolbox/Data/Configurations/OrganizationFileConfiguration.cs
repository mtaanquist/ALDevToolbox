using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationFileConfiguration : IEntityTypeConfiguration<OrganizationFile>
{

    public void Configure(EntityTypeBuilder<OrganizationFile> entity)
    {
        entity.ToTable("organization_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.MustacheEnabled).HasColumnName("mustache_enabled").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Ordering });
        entity.HasIndex(e => new { e.OrganizationId, e.Path }).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
