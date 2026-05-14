using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SnippetSuggestionConfiguration : IEntityTypeConfiguration<SnippetSuggestion>
{

    public void Configure(EntityTypeBuilder<SnippetSuggestion> entity)
    {
        entity.ToTable("snippet_suggestions");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.SuggestedByUserId).HasColumnName("suggested_by_user_id");
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.Keywords).HasColumnName("keywords").IsRequired();
        entity.Property(e => e.Decision).HasColumnName("decision").HasConversion<string>().IsRequired();
        entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
        entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
        entity.Property(e => e.DecidedByUserId).HasColumnName("decided_by_user_id");
        entity.Property(e => e.DecisionNote).HasColumnName("decision_note");
        entity.Property(e => e.ApprovedSnippetId).HasColumnName("approved_snippet_id");
        entity.HasIndex(e => new { e.OrganizationId, e.Decision });
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.SuggestedByUser)
            .WithMany()
            .HasForeignKey(e => e.SuggestedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(e => e.DecidedByUser)
            .WithMany()
            .HasForeignKey(e => e.DecidedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(e => e.ApprovedSnippet)
            .WithMany()
            .HasForeignKey(e => e.ApprovedSnippetId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasMany(e => e.Files)
            .WithOne(f => f.Suggestion!)
            .HasForeignKey(f => f.SnippetSuggestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
