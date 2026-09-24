using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class RepositoryMergedPullRequestConfiguration : IEntityTypeConfiguration<OeRepositoryMergedPullRequest>
{
    public void Configure(EntityTypeBuilder<OeRepositoryMergedPullRequest> entity)
    {
        entity.ToTable("oe_repository_merged_pull_requests");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id").IsRequired();
        entity.Property(e => e.Number).HasColumnName("number").IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(500).IsRequired();
        entity.Property(e => e.BaseBranch).HasColumnName("base_branch").HasMaxLength(255).IsRequired();
        entity.Property(e => e.MergeSha).HasColumnName("merge_sha").HasMaxLength(40).IsRequired();
        entity.Property(e => e.MergedAt).HasColumnName("merged_at").IsRequired();
        entity.Property(e => e.AuthorLogin).HasColumnName("author_login").HasMaxLength(255).IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // A redelivered webhook lands on the same row rather than a second one.
        entity.HasIndex(e => new { e.ProjectRepositoryId, e.Number })
            .IsUnique()
            .HasDatabaseName("ux_oe_repository_merged_pull_requests_repo_number");
    }
}
