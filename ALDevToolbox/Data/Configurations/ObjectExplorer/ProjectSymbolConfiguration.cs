using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectSymbolConfiguration : IEntityTypeConfiguration<ProjectSymbol>
{
    public void Configure(EntityTypeBuilder<ProjectSymbol> entity)
    {
        entity.ToTable("oe_project_symbols");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(260).IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.ContentLength).HasColumnName("content_length").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Project -> Symbols relationship (FK project_id, cascade) is
        // configured on the Project side. Per-project uniqueness on the file
        // name makes a re-upload of the same package a replace, not a duplicate.
        entity.HasIndex(e => new { e.ProjectId, e.FileName })
            .IsUnique()
            .HasDatabaseName("ix_oe_project_symbols_project_file");
    }
}
