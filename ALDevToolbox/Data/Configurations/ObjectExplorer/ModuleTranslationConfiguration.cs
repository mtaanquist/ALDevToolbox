using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleTranslationConfiguration : IEntityTypeConfiguration<ModuleTranslation>
{
    public void Configure(EntityTypeBuilder<ModuleTranslation> entity)
    {
        entity.ToTable("oe_module_translations");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.LanguageCode).HasColumnName("language_code").IsRequired();
        entity.Property(e => e.TransUnitId).HasColumnName("trans_unit_id").IsRequired();
        entity.Property(e => e.SourceText).HasColumnName("source_text").IsRequired();
        entity.Property(e => e.TargetText).HasColumnName("target_text").IsRequired();
        entity.Property(e => e.TargetState).HasColumnName("target_state");
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.ObjectKind).HasColumnName("object_kind");
        entity.Property(e => e.ObjectName).HasColumnName("object_name");
        entity.Property(e => e.SubKind).HasColumnName("sub_kind");
        entity.Property(e => e.SubName).HasColumnName("sub_name");
        entity.Property(e => e.PropertyName).HasColumnName("property_name");
        entity.Property(e => e.DeveloperNote).HasColumnName("developer_note");
        entity.Property(e => e.SymbolId).HasColumnName("symbol_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany()
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // SET NULL on symbol delete: a module re-import can rewrite the
        // symbol table without us wanting to lose the translation row.
        entity.HasOne(e => e.Symbol)
            .WithMany()
            .HasForeignKey(e => e.SymbolId)
            .OnDelete(DeleteBehavior.SetNull);

        // Bulk reads per (module, language) — the clobber path's DELETE
        // predicate and the per-module language chip query.
        entity.HasIndex(e => new { e.ModuleId, e.LanguageCode })
            .HasDatabaseName("ix_oe_module_translations_module_lang");

        // Clobber-by-trans-unit-id replacement — also acts as a sanity
        // guard against duplicate trans-unit ids inside a single XLIFF.
        entity.HasIndex(e => new { e.ModuleId, e.LanguageCode, e.TransUnitId })
            .IsUnique()
            .HasDatabaseName("ux_oe_module_translations_module_lang_unit");

        // Reverse lookup "what translations exist for this label / field?".
        // Partial filter via raw SQL in the migration (EF can't express it).
        entity.HasIndex(e => e.SymbolId)
            .HasDatabaseName("ix_oe_module_translations_symbol");

        // Issue #169 future: "find a field by its caption". Composite
        // (org, object_kind, object_name) supports a tenant-scoped lookup
        // without dragging in every release's translations.
        entity.HasIndex(e => new { e.OrganizationId, e.ObjectKind, e.ObjectName })
            .HasDatabaseName("ix_oe_module_translations_org_obj");
    }
}
