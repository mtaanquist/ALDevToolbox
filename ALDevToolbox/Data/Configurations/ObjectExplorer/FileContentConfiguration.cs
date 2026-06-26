using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class FileContentConfiguration : IEntityTypeConfiguration<FileContent>
{
    public void Configure(EntityTypeBuilder<FileContent> entity)
    {
        entity.ToTable("oe_file_contents");

        // Content-addressed: the hash IS the key. No surrogate id, no
        // organization_id — this table is deliberately cross-tenant shared and
        // must never be added to the multi-tenant query filter (see the entity
        // doc and AppDbContext.OnModelCreating).
        entity.HasKey(e => e.ContentHash);
        entity.Property(e => e.ContentHash).HasColumnName("content_hash");
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.ContentLength).HasColumnName("content_length").IsRequired();
        entity.Property(e => e.LineCount).HasColumnName("line_count").IsRequired();

        // Backs the "File content" grep (ObjectSearchService.SearchContentInReleaseAsync),
        // which runs `content ILIKE '%term%'` against this store. Without it the
        // search sequentially scans every distinct blob — slow on a large
        // catalogue, and the most-used Object Explorer search. A pg_trgm GIN
        // index lets the planner trigram-match instead, the same pattern
        // translation_memory.source_text uses and what the retired
        // base_app_files table carried on its content column. Indexing the
        // deduplicated store (one row per distinct blob, shared across
        // releases) means each blob is indexed once, not once per file
        // reference. pg_trgm is enabled on the model in AppDbContext.
        // Note: only patterns of >= 3 chars use the index (a trigram needs
        // three characters); shorter terms are rejected up front by the
        // service so they never fall back to a full scan.
        entity.HasIndex(e => e.Content)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_oe_file_contents_content_trgm");
    }
}
