using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class TranslationMemoryVoteConfiguration : IEntityTypeConfiguration<TranslationMemoryVote>
{
    public void Configure(EntityTypeBuilder<TranslationMemoryVote> entity)
    {
        entity.ToTable("translation_memory_votes");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.EntryId).HasColumnName("entry_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.Value).HasColumnName("value").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting an entry removes its votes; deleting a user removes theirs.
        entity.HasOne(e => e.Entry)
            .WithMany()
            .HasForeignKey(e => e.EntryId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One vote per (entry, user) — the upsert key.
        entity.HasIndex(e => new { e.EntryId, e.UserId })
            .IsUnique()
            .HasDatabaseName("ux_translation_memory_votes_entry_user");
    }
}
