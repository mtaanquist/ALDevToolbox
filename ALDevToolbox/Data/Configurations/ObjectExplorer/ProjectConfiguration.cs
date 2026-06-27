using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> entity)
    {
        entity.ToTable("oe_projects");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.DefaultArtifactCountry).HasColumnName("default_artifact_country").HasMaxLength(20);
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        entity.Property(e => e.DiscoveredExtensionsJson).HasColumnName("discovered_extensions_json");
        entity.Property(e => e.DiscoveredAt).HasColumnName("discovered_at");
        entity.Property(e => e.DiscoveryError).HasColumnName("discovery_error");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The owner. SET NULL on delete so removing a user doesn't cascade-delete
        // their projects — they become admin-managed until reassigned.
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.Repositories)
            .WithOne(r => r.Project!)
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Symbols)
            .WithOne(s => s.Project!)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Builds)
            .WithOne(b => b.Project!)
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Per-org name uniqueness on active rows so the picker doesn't show
        // duplicates. Case-INsensitive (lower(name)) to match the service's
        // case-insensitive pre-check — otherwise "CRONUS" and "cronus" pass the index
        // but the app rejects them, an inconsistency. EF can't model a functional
        // index, so it's created via raw SQL in the migration (and intentionally
        // not declared here). See issue #432.
    }
}
