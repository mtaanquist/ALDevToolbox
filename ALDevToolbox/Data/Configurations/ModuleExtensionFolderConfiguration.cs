using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ModuleExtensionFolderConfiguration : IEntityTypeConfiguration<ModuleExtensionFolder>
{

    public void Configure(EntityTypeBuilder<ModuleExtensionFolder> entity)
    {
        entity.ToTable("module_extension_folders");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.ParentFolderId).HasColumnName("parent_folder_id");
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.HasIndex(e => new { e.ModuleId, e.ParentFolderId, e.Ordering });
        entity.HasIndex(e => new { e.ParentFolderId, e.Path })
            .IsUnique()
            .HasFilter("parent_folder_id IS NOT NULL")
            .HasDatabaseName("ix_module_extension_folders_sibling_unique");
        entity.HasIndex(e => new { e.ModuleId, e.Path })
            .IsUnique()
            .HasFilter("parent_folder_id IS NULL")
            .HasDatabaseName("ix_module_extension_folders_root_unique");

        entity.HasOne(e => e.ParentFolder)
            .WithMany(f => f!.Folders)
            .HasForeignKey(e => e.ParentFolderId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Files)
            .WithOne(f => f.Folder!)
            .HasForeignKey(f => f.ModuleExtensionFolderId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
