using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RecipeFileConfiguration : IEntityTypeConfiguration<RecipeFile>
{

    public void Configure(EntityTypeBuilder<RecipeFile> entity)
    {
        entity.ToTable("recipe_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.RecipeId).HasColumnName("recipe_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.RelativePath).HasColumnName("relative_path").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.RecipeId, e.Ordering });
    }
}
