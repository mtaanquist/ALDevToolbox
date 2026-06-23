using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{

    public void Configure(EntityTypeBuilder<Recipe> entity)
    {
        entity.ToTable("recipes");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.Keywords).HasColumnName("keywords").IsRequired();
        // Stored as int so the column matches the historical Postgres `type`
        // column we add in the rename migration with a default value of 0
        // (Snippet) for existing rows.
        entity.Property(e => e.Type).HasColumnName("type").HasConversion<int>().IsRequired();
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.Instructions).HasColumnName("instructions");
        entity.Property(e => e.MinimumApplicationVersionId).HasColumnName("minimum_application_version_id");
        entity.Property(e => e.EstimatedValueHours).HasColumnName("estimated_value_hours").HasPrecision(8, 2);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.HasIndex(e => new { e.OrganizationId, e.Title }).IsUnique();
        // Drives the type chip-row filter on /cookbook and /admin/cookbook.
        entity.HasIndex(e => new { e.OrganizationId, e.Type });
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        // SetNull on delete so soft-deleting a catalogue row doesn't cascade-
        // delete recipes pointing at it. The badge collapses naturally when
        // the FK reads back as null; admins can reassign on the edit form.
        entity.HasOne(e => e.MinimumApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.MinimumApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasMany(e => e.Files)
            .WithOne(f => f.Recipe!)
            .HasForeignKey(f => f.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
