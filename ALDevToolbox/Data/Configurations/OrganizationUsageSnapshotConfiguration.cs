using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationUsageSnapshotConfiguration : IEntityTypeConfiguration<OrganizationUsageSnapshot>
{
    public void Configure(EntityTypeBuilder<OrganizationUsageSnapshot> entity)
    {
        entity.ToTable("organization_usage_snapshots");
        // One snapshot per org: organization_id is both PK and FK.
        entity.HasKey(e => e.OrganizationId);
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").ValueGeneratedNever();
        entity.Property(e => e.LogicalBytes).HasColumnName("logical_bytes").IsRequired();
        entity.Property(e => e.IndexBytes).HasColumnName("index_bytes").IsRequired();
        entity.Property(e => e.ComputedAt).HasColumnName("computed_at").IsRequired();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        // Cross-org infrastructure table, written off-request by the snapshot
        // scheduler and read per-org by DatabaseUsageService; no query filter.
    }
}
