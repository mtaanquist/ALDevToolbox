using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ModuleExtensionFileConfiguration : IEntityTypeConfiguration<ModuleExtensionFile>
{

    public void Configure(EntityTypeBuilder<ModuleExtensionFile> entity)
    {
        entity.ToTable("module_extension_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleExtensionFolderId).HasColumnName("module_extension_folder_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.IsExample).HasColumnName("is_example").IsRequired();
        entity.HasIndex(e => new { e.ModuleExtensionFolderId, e.Ordering });
        entity.HasIndex(e => new { e.ModuleExtensionFolderId, e.Path }).IsUnique();
    }
}
