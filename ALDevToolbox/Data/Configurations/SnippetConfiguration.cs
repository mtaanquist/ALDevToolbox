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
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.HasIndex(e => new { e.OrganizationId, e.Title }).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(e => e.Files)
            .WithOne(f => f.Snippet!)
            .HasForeignKey(f => f.SnippetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
