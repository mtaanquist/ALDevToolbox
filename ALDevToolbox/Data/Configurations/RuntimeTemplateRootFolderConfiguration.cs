using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RuntimeTemplateRootFolderConfiguration : IEntityTypeConfiguration<RuntimeTemplateRootFolder>
{

    public void Configure(EntityTypeBuilder<RuntimeTemplateRootFolder> entity)
    {
        entity.ToTable("runtime_template_root_folders");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.RuntimeTemplateId).HasColumnName("runtime_template_id").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").HasMaxLength(400).IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.RuntimeTemplateId, e.Ordering });
        entity.HasIndex(e => new { e.RuntimeTemplateId, e.Path }).IsUnique();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
