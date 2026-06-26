using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ModuleObjectConfiguration : IEntityTypeConfiguration<ModuleObject>
{
    public void Configure(EntityTypeBuilder<ModuleObject> entity)
    {
        entity.ToTable("oe_module_objects");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.ObjectId).HasColumnName("object_id");
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Namespace).HasColumnName("namespace");
        entity.Property(e => e.VersionList).HasColumnName("version_list");
        entity.Property(e => e.ExtendsAppId).HasColumnName("extends_app_id");
        entity.Property(e => e.ExtendsObjectName).HasColumnName("extends_object_name");
        entity.Property(e => e.SourceTableName).HasColumnName("source_table_name");
        entity.Property(e => e.SourceFileId).HasColumnName("source_file_id");
        entity.Property(e => e.LineNumber).HasColumnName("line_number").IsRequired();
        entity.Property(e => e.ObsoleteState).HasColumnName("obsolete_state");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Module)
            .WithMany(m => m.Objects)
            .HasForeignKey(e => e.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Files keep their row when an object is removed (SetNull); the object itself is
        // cascade-deleted with the Module so this matters only in test/admin scenarios.
        entity.HasOne(e => e.SourceFile)
            .WithMany()
            .HasForeignKey(e => e.SourceFileId)
            .OnDelete(DeleteBehavior.SetNull);

        // Primary list ordering for the browser.
        entity.HasIndex(e => new { e.ModuleId, e.Kind, e.Name })
            .HasDatabaseName("ix_oe_module_objects_module_kind_name");

        // ID-based lookup (when the symbol package carries an ObjectId).
        entity.HasIndex(e => new { e.ModuleId, e.Kind, e.ObjectId })
            .HasDatabaseName("ix_oe_module_objects_module_kind_objectid");

        // Substring/"Tell Me" object search runs `name ILIKE '%term%'` across a
        // release's modules. With ~2M object rows in a fully-loaded catalogue
        // the btree indexes above can't back an unanchored substring, so the
        // search fell back to a sequential scan (issue: Object Explorer search
        // takes 3-4s on a 232-release install). These pg_trgm GIN indexes give
        // the planner an index path for the ILIKE — same pattern the Translator
        // uses on translation_memory.source_text, and what the retired
        // base_app_files table carried on object_name. version_list is indexed
        // too because the substring/glob search ORs it in for C/AL tagging.
        // pg_trgm is enabled on the model in AppDbContext.OnModelCreating.
        entity.HasIndex(e => e.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_oe_module_objects_name_trgm");

        entity.HasIndex(e => e.VersionList)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_oe_module_objects_version_list_trgm");
    }
}
