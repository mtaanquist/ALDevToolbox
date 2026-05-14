using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class WorkspaceExtensionConfiguration : IEntityTypeConfiguration<WorkspaceExtension>
{

    public void Configure(EntityTypeBuilder<WorkspaceExtension> entity)
    {
        entity.ToTable("workspace_extensions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.TemplateId).HasColumnName("template_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.NameTemplate).HasColumnName("name_template").IsRequired();
        entity.Property(e => e.Required).HasColumnName("required").IsRequired();
        entity.Property(e => e.Application).HasColumnName("application");
        entity.Property(e => e.Runtime).HasColumnName("runtime");
        entity.Property(e => e.IdRangeFrom).HasColumnName("id_range_from");
        entity.Property(e => e.IdRangeTo).HasColumnName("id_range_to");
        entity.HasIndex(e => new { e.OrganizationId, e.TemplateId, e.Ordering });
        entity.HasIndex(e => new { e.TemplateId, e.Path }).IsUnique();

        entity.HasMany(e => e.Folders)
            .WithOne(f => f.Extension!)
            .HasForeignKey(f => f.WorkspaceExtensionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Dependencies)
            .WithOne(d => d.Extension!)
            .HasForeignKey(d => d.WorkspaceExtensionId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
