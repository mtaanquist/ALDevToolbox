using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleReferenceConfiguration : IEntityTypeConfiguration<ModuleReference>
{
    public void Configure(EntityTypeBuilder<ModuleReference> entity)
    {
        entity.ToTable("oe_module_references");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.SourceObjectId).HasColumnName("source_object_id").IsRequired();
        entity.Property(e => e.TargetAppId).HasColumnName("target_app_id").IsRequired();
        entity.Property(e => e.TargetObjectKind).HasColumnName("target_object_kind").IsRequired();
        entity.Property(e => e.TargetObjectId).HasColumnName("target_object_id");
        entity.Property(e => e.TargetObjectName).HasColumnName("target_object_name").IsRequired();
        entity.Property(e => e.ReferenceKind).HasColumnName("reference_kind").IsRequired();
        entity.Property(e => e.LineNumber).HasColumnName("line_number");
        entity.Property(e => e.ColumnNumber).HasColumnName("column_number");
        entity.Property(e => e.TargetMemberName).HasColumnName("target_member_name");
        entity.Property(e => e.TargetMemberKind).HasColumnName("target_member_kind");
        entity.Property(e => e.TargetSymbolId).HasColumnName("target_symbol_id");
        entity.Property(e => e.TargetVariableId).HasColumnName("target_variable_id");
        entity.Property(e => e.SourceSymbolId).HasColumnName("source_symbol_id");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.References)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.SourceObject)
            .WithMany()
            .HasForeignKey(e => e.SourceObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.TargetSymbol)
            .WithMany()
            .HasForeignKey(e => e.TargetSymbolId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.TargetVariable)
            .WithMany()
            .HasForeignKey(e => e.TargetVariableId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.SourceSymbol)
            .WithMany()
            .HasForeignKey(e => e.SourceSymbolId)
            .OnDelete(DeleteBehavior.SetNull);

        // Primary find-references query: target triplet → matching rows. Used by the
        // recursive-CTE join in ObjectExplorerService.FindReferencesAsync (PR 4).
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_references_target_id");

        // Fallback when ObjectId is null on the symbol-package side (interfaces, some
        // extensions) — match by name instead.
        entity.HasIndex(e => new { e.TargetAppId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_references_target_name");

        // Module-scoped name match for the find-references / Used-by queries.
        // Those queries match a target across a Release's *visible module chain*,
        // so they filter `module_id = ANY(<winning modules>)`. The id-branch is
        // served by ix_oe_module_references_module_target below; this is its
        // name-branch twin, used when the target has no numeric object id
        // (interfaces, some extensions). Without it the name-branch falls back
        // to ix_oe_module_references_target_name, which is keyed on the *app*
        // (stable across every Release) and so matches the target in all
        // imported Releases — a cross-release fan-out that read ~200k rows to
        // return a few dozen and tipped the query into a parallel plan that
        // exhausted /dev/shm. Partial on the null-id rows so it stays tiny
        // (most references resolve to an id). See ReferenceQueryService
        // (BuildUsedByAsync / FindReferencesAsync) and .design/object-explorer.md.
        entity.HasIndex(e => new { e.ModuleId, e.TargetObjectKind, e.TargetObjectName })
            .HasDatabaseName("ix_oe_module_references_module_target_name")
            .HasFilter("\"target_object_id\" IS NULL");

        // Member-scoped find-references: who calls Customer.Validate()? who reads
        // Customer."No."? Used once method-call extraction lands in phase 2;
        // partial-filtered on rows where target_member_name is set so it stays
        // small until then.
        entity.HasIndex(e => new
            {
                e.TargetAppId,
                e.TargetObjectKind,
                e.TargetObjectId,
                e.TargetMemberName,
                e.TargetMemberKind,
            })
            .HasDatabaseName("ix_oe_module_references_target_member")
            .HasFilter("\"target_member_name\" IS NOT NULL");

        // Reverse direction: enumerate references originating from a specific object,
        // used when displaying outbound references from the inspector panel.
        entity.HasIndex(e => e.SourceObjectId)
            .HasDatabaseName("ix_oe_module_references_source_object");

        // Module-scoped resolution: the C/AL import's id→name post-pass
        // (CalImportService.ResolveTargetsByIdAsync) UPDATEs every reference in
        // one module by joining on (module_id, target_object_kind,
        // target_object_id). Without this the UPDATE seq-scans the whole table
        // — which accumulates across every release — and tips past the command
        // timeout on a large import. Also serves the virtual-table UPDATEs.
        entity.HasIndex(e => new { e.ModuleId, e.TargetObjectKind, e.TargetObjectId })
            .HasDatabaseName("ix_oe_module_references_module_target");

        // Right-click "Find references" on a global variable: returns
        // every variable_use row pointing at the variable's DB id.
        // Partial-filtered on non-null so it stays small relative to
        // the millions of object-level / member-level rows that don't
        // carry a variable id. See .design/al-reference-extractor-
        // refactor.md step 6.
        entity.HasIndex(e => e.TargetVariableId)
            .HasDatabaseName("ix_oe_module_references_target_variable")
            .HasFilter("\"target_variable_id\" IS NOT NULL");

        // Forward-edge "what does this procedure call?" — partial-filtered so
        // it only carries rows emitted from inside a procedure body, which is
        // a small minority of all references on a large catalog import.
        entity.HasIndex(e => e.SourceSymbolId)
            .HasDatabaseName("ix_oe_module_references_source_symbol")
            .HasFilter("\"source_symbol_id\" IS NOT NULL");
    }
}
