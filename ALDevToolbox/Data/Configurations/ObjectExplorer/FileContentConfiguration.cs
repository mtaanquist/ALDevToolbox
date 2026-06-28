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

        // pg_trgm GIN index on the deduplicated content store (one row per
        // distinct blob; pg_trgm enabled on the model in AppDbContext).
        //
        // NOTE: the "File content" grep (ObjectSearchService.SearchContentInReleaseAsync)
        // no longer relies on this index. Because the store is shared across
        // every imported Release, a global `content ILIKE '%term%'` matched the
        // term in all Releases at once and fanned out (~28 s on a full
        // catalogue); the search now scopes to the queried Release's own blobs
        // first and ILIKE-scans those (~2 s), a plan the planner won't combine
        // with this trigram (it badly under-estimates ILIKE selectivity, so it
        // never bitmap-ANDs the two). The index is retained as insurance for a
        // possible future Release-scoped trigram approach (a composite
        // btree_gin (content, content_hash) that the planner *would* use, gated
        // on a data-model change); if that's ruled out, this becomes dead weight
        // and a candidate for removal. translation_memory.source_text still uses
        // the same single-tenant trigram pattern.
        entity.HasIndex(e => e.Content)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("ix_oe_file_contents_content_trgm");
    }
}
