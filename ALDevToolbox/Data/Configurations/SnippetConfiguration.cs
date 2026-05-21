using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class SnippetConfiguration : IEntityTypeConfiguration<Snippet>
{

    public void Configure(EntityTypeBuilder<Snippet> entity)
    {
        entity.ToTable("snippets");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.Keywords).HasColumnName("keywords").IsRequired();
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.Instructions).HasColumnName("instructions");
        entity.Property(e => e.MinimumApplicationVersionId).HasColumnName("minimum_application_version_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.HasIndex(e => new { e.OrganizationId, e.Title }).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        // SetNull on delete so soft-deleting a catalogue row doesn't cascade-
        // delete snippets pointing at it. The badge collapses naturally when
        // the FK reads back as null; admins can reassign on the edit form.
        entity.HasOne(e => e.MinimumApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.MinimumApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasMany(e => e.Files)
            .WithOne(f => f.Snippet!)
            .HasForeignKey(f => f.SnippetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
