using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class BaseAppSymbolConfiguration : IEntityTypeConfiguration<BaseAppSymbol>
{
    public void Configure(EntityTypeBuilder<BaseAppSymbol> entity)
    {
        entity.ToTable("base_app_symbols");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.VersionId).HasColumnName("version_id").IsRequired();
        entity.Property(e => e.FileId).HasColumnName("file_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Signature).HasColumnName("signature");
        entity.Property(e => e.FieldId).HasColumnName("field_id");
        entity.Property(e => e.LineNumber).HasColumnName("line_number").IsRequired();
        entity.Property(e => e.ColumnStart).HasColumnName("column_start").IsRequired();
        entity.Property(e => e.ColumnEnd).HasColumnName("column_end").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.File)
            .WithMany(f => f.Symbols)
            .HasForeignKey(e => e.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Click-position lookup: given (file_id, line_number), find the
        // declaration the user right-clicked on.
        entity.HasIndex(e => new { e.FileId, e.LineNumber });

        // References-query lookup: filtered by (version_id, kind, name).
        // The name is stored as-is (case preserved) but the references query
        // lower-cases both sides — we add the lower-case functional index in
        // raw SQL inside the migration.
        entity.HasIndex(e => new { e.VersionId, e.Kind, e.Name });
    }
}
