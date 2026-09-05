using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubRepositoryCandidateConfiguration : IEntityTypeConfiguration<GitHubRepositoryCandidate>
{
    public void Configure(EntityTypeBuilder<GitHubRepositoryCandidate> entity)
    {
        entity.ToTable("github_repository_candidates");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.FullName).HasColumnName("full_name").HasMaxLength(300).IsRequired();
        entity.Property(e => e.HtmlUrl).HasColumnName("html_url").HasMaxLength(2000).IsRequired();
        entity.Property(e => e.CloneUrl).HasColumnName("clone_url").HasMaxLength(2000).IsRequired();
        entity.Property(e => e.DefaultBranch).HasColumnName("default_branch").HasMaxLength(255).IsRequired();
        entity.Property(e => e.AppName).HasColumnName("app_name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(100).IsRequired();
        entity.Property(e => e.AppJsonPath).HasColumnName("app_json_path").HasMaxLength(500).IsRequired();
        entity.Property(e => e.DiscoveredAt).HasColumnName("discovered_at").IsRequired();
        entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
        entity.Property(e => e.IgnoredAt).HasColumnName("ignored_at");
        entity.Property(e => e.IgnoredByUserId).HasColumnName("ignored_by_user_id");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per repository per organisation: the sweep upserts on this.
        entity.HasIndex(e => new { e.OrganizationId, e.FullName })
            .IsUnique()
            .HasDatabaseName("ux_github_repository_candidates_org_full_name");
    }
}
