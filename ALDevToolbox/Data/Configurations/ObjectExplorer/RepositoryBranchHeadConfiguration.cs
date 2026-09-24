using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class RepositoryBranchHeadConfiguration : IEntityTypeConfiguration<OeRepositoryBranchHead>
{
    public void Configure(EntityTypeBuilder<OeRepositoryBranchHead> entity)
    {
        entity.ToTable("oe_repository_branch_heads");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id").IsRequired();
        entity.Property(e => e.Branch).HasColumnName("branch").HasMaxLength(255).IsRequired();
        entity.Property(e => e.HeadSha).HasColumnName("head_sha").HasMaxLength(40).IsRequired();
        entity.Property(e => e.PushedAt).HasColumnName("pushed_at").IsRequired();
        entity.Property(e => e.PusherLogin).HasColumnName("pusher_login").HasMaxLength(255).IsRequired();
        entity.Property(e => e.Forced).HasColumnName("forced").IsRequired();
        entity.Property(e => e.CommitCount).HasColumnName("commit_count").IsRequired();
        entity.Property(e => e.CommitsJson).HasColumnName("commits_json").HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.IsDefaultBranch).HasColumnName("is_default_branch").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // A head is a fact about one solution repository; removing the repository
        // from the solution removes what we heard about its branches.
        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per branch: the upsert key, and the freshness read's lookup.
        entity.HasIndex(e => new { e.ProjectRepositoryId, e.Branch })
            .IsUnique()
            .HasDatabaseName("ux_oe_repository_branch_heads_repo_branch");
    }
}
