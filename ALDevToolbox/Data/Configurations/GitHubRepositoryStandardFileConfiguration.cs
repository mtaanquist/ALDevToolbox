using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubRepositoryStandardFileConfiguration
    : IEntityTypeConfiguration<GitHubRepositoryStandardFile>
{
    public void Configure(EntityTypeBuilder<GitHubRepositoryStandardFile> entity)
    {
        entity.ToTable("github_repository_standard_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Path).HasColumnName("path").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Ordering });
        // One row per path per organisation: two standards writing the same
        // file into a repository is a contradiction, not a merge.
        entity.HasIndex(e => new { e.OrganizationId, e.Path }).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
