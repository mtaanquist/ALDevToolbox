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
    }
}
