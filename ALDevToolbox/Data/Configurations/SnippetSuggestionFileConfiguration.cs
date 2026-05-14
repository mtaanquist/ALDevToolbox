using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SnippetSuggestionFileConfiguration : IEntityTypeConfiguration<SnippetSuggestionFile>
{

    public void Configure(EntityTypeBuilder<SnippetSuggestionFile> entity)
    {
        entity.ToTable("snippet_suggestion_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.SnippetSuggestionId).HasColumnName("snippet_suggestion_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.SnippetSuggestionId, e.Ordering });
    }
}
