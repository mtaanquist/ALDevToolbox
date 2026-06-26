using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildLogConfiguration : IEntityTypeConfiguration<ProjectBuildLog>
{
    public void Configure(EntityTypeBuilder<ProjectBuildLog> entity)
    {
        entity.ToTable("oe_project_build_logs");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id");
        entity.Property(e => e.Section).HasColumnName("section").HasMaxLength(250).IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The build relationship is configured from ProjectBuild
        // (HasMany(e => e.Logs)); don't redeclare it.

        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.ProjectBuildId).HasDatabaseName("ix_oe_project_build_logs_build");
    }
}
