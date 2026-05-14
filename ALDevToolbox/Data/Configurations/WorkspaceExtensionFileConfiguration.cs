using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class WorkspaceExtensionFileConfiguration : IEntityTypeConfiguration<WorkspaceExtensionFile>
{
    private readonly IOrganizationContext _orgContext;
    public WorkspaceExtensionFileConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<WorkspaceExtensionFile> entity)
    {
        entity.ToTable("workspace_extension_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.WorkspaceExtensionFolderId).HasColumnName("workspace_extension_folder_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.IsExample).HasColumnName("is_example").IsRequired();
        entity.HasIndex(e => new { e.WorkspaceExtensionFolderId, e.Ordering });
        entity.HasIndex(e => new { e.WorkspaceExtensionFolderId, e.Path }).IsUnique();
        entity.ScopeToOrganization(_orgContext);
    }
}
