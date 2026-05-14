using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ApplicationVersionConfiguration : IEntityTypeConfiguration<ApplicationVersion>
{

    public void Configure(EntityTypeBuilder<ApplicationVersion> entity)
    {
        entity.ToTable("application_versions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Key).HasColumnName("key").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Key }).IsUnique();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Application).HasColumnName("application").IsRequired();
        entity.Property(e => e.Runtime).HasColumnName("runtime").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
