using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildRepoCommitConfiguration : IEntityTypeConfiguration<ProjectBuildRepoCommit>
{
    public void Configure(EntityTypeBuilder<ProjectBuildRepoCommit> entity)
    {
        entity.ToTable("oe_project_build_repo_commits");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id");
        entity.Property(e => e.RepoUrl).HasColumnName("repo_url").HasMaxLength(2000).IsRequired();
        entity.Property(e => e.RepoDisplayName).HasColumnName("repo_display_name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.CommitHash).HasColumnName("commit_hash").HasMaxLength(64).IsRequired();
        entity.Property(e => e.CommittedAt).HasColumnName("committed_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The build relationship is configured from ProjectBuild
        // (HasMany(e => e.RepoCommits)); don't redeclare it.

        // SET NULL so removing a repo from the project doesn't erase the
        // provenance of past builds that used it (repo_url/display stay legible).
        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.ProjectBuildId).HasDatabaseName("ix_oe_project_build_repo_commits_build");
    }
}
