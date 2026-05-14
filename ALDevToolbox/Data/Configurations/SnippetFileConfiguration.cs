using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SnippetFileConfiguration : IEntityTypeConfiguration<SnippetFile>
{
    private readonly IOrganizationContext _orgContext;
    public SnippetFileConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<SnippetFile> entity)
    {
        entity.ToTable("snippet_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.SnippetId).HasColumnName("snippet_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.SnippetId, e.Ordering });
        entity.ScopeToOrganization(_orgContext);
    }
}
