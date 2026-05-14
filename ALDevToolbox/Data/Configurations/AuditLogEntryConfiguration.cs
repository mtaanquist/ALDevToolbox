using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> entity)
    {
        entity.ToTable("audit_log");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
        entity.Property(e => e.ChangedBy).HasColumnName("changed_by").IsRequired();
        entity.Property(e => e.ChangedByUserId).HasColumnName("changed_by_user_id");
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
        entity.Property(e => e.EntityType)
            .HasColumnName("entity_type")
            .HasConversion<string>()
            .IsRequired();
        entity.Property(e => e.EntityId).HasColumnName("entity_id").IsRequired();
        entity.Property(e => e.Action)
            .HasColumnName("action")
            .HasConversion<string>()
            .IsRequired();
        entity.Property(e => e.SnapshotJson).HasColumnName("snapshot_json");
        entity.HasIndex(e => new { e.EntityType, e.EntityId, e.Timestamp })
            .HasDatabaseName("ix_audit_log_entity_timestamp");
        entity.HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_audit_log_timestamp");
        entity.HasIndex(e => new { e.OrganizationId, e.Timestamp })
            .HasDatabaseName("ix_audit_log_org_timestamp");
        // Audit history is durable: refuse to delete an organisation or user
        // while audit rows still reference them. The previous SetNull behaviour
        // wiped both subject and actor at once on org delete, leaving rows with
        // no recoverable provenance. A deliberate "anonymise" flow can surface
        // later; we'd rather refuse the delete than silently lose the audit
        // chain.
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(e => e.ChangedByUser)
            .WithMany()
            .HasForeignKey(e => e.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        // AuditLog scoping is service-layer (AuditService filters by org
        // explicitly) — we don't apply a query filter here because seed and
        // bootstrap inserts can have a null OrganizationId.
    }
}
