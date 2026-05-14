using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class WorkspaceExtensionFolderConfiguration : IEntityTypeConfiguration<WorkspaceExtensionFolder>
{

    public void Configure(EntityTypeBuilder<WorkspaceExtensionFolder> entity)
    {
        entity.ToTable("workspace_extension_folders");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        // Denormalised onto every row so leaf queries don't have to walk
        // the parent chain to scope by extension. The service-layer
        // reconciliation keeps it in lock-step with the parent's value.
        entity.Property(e => e.WorkspaceExtensionId).HasColumnName("workspace_extension_id").IsRequired();
        entity.Property(e => e.ParentFolderId).HasColumnName("parent_folder_id");
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.HasIndex(e => new { e.WorkspaceExtensionId, e.ParentFolderId, e.Ordering });
        // Sibling-uniqueness: within a non-null parent, path is unique.
        entity.HasIndex(e => new { e.ParentFolderId, e.Path })
            .IsUnique()
            .HasFilter("parent_folder_id IS NOT NULL")
            .HasDatabaseName("ix_workspace_extension_folders_sibling_unique");
        // Sibling-uniqueness at the root: parent_folder_id IS NULL slice.
        entity.HasIndex(e => new { e.WorkspaceExtensionId, e.Path })
            .IsUnique()
            .HasFilter("parent_folder_id IS NULL")
            .HasDatabaseName("ix_workspace_extension_folders_root_unique");

        entity.HasOne(e => e.ParentFolder)
            .WithMany(f => f!.Folders)
            .HasForeignKey(e => e.ParentFolderId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Files)
            .WithOne(f => f.Folder!)
            .HasForeignKey(f => f.WorkspaceExtensionFolderId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
