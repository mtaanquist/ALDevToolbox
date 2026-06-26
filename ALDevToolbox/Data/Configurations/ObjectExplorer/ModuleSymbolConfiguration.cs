using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleSymbolConfiguration : IEntityTypeConfiguration<ModuleSymbol>
{
    public void Configure(EntityTypeBuilder<ModuleSymbol> entity)
    {
        entity.ToTable("oe_module_symbols");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.ObjectId).HasColumnName("object_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Signature).HasColumnName("signature");
        entity.Property(e => e.ReturnType).HasColumnName("return_type");
        entity.Property(e => e.FieldId).HasColumnName("field_id");
        entity.Property(e => e.LineNumber).HasColumnName("line_number").IsRequired();
        entity.Property(e => e.ColumnStart).HasColumnName("column_start").IsRequired();
        entity.Property(e => e.ColumnEnd).HasColumnName("column_end").IsRequired();
        entity.Property(e => e.EndLine).HasColumnName("end_line");
        entity.Property(e => e.EndColumn).HasColumnName("end_column");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.Symbols)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Object)
            .WithMany(o => o.Symbols)
            .HasForeignKey(e => e.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Inspector panel: enumerate symbols of an object in declaration order.
        entity.HasIndex(e => new { e.ObjectId, e.LineNumber })
            .HasDatabaseName("ix_oe_module_symbols_object_line");

        // Find-references bootstrap: name-keyed lookup within a module (case-insensitive
        // index added via raw SQL in the migration on lower(name)).
        entity.HasIndex(e => new { e.ModuleId, e.Kind, e.Name })
            .HasDatabaseName("ix_oe_module_symbols_module_kind_name");

        // Procedure search runs `name ILIKE '%term%'` across a release's
        // modules (ObjectSearchService.SearchProceduresInReleaseAsync). Same
        // unanchored-substring problem as oe_module_objects.name — back it with
        // a pg_trgm GIN index so it doesn't sequential-scan the symbol table.
        entity.HasIndex(e => e.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_oe_module_symbols_name_trgm");
    }
}
