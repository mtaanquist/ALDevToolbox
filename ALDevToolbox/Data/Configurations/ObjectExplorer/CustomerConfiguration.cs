using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> entity)
    {
        entity.ToTable("oe_customers");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.DefaultArtifactCountry).HasColumnName("default_artifact_country").HasMaxLength(20);
        entity.Property(e => e.AutoBuildEnabled).HasColumnName("auto_build_enabled").HasDefaultValue(false).IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Repositories)
            .WithOne(r => r.Customer!)
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Symbols)
            .WithOne(s => s.Customer!)
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Per-org name uniqueness on active rows so the picker doesn't show
        // duplicates. Case-INsensitive (lower(name)) to match the service's
        // case-insensitive pre-check — otherwise "Acme" and "acme" pass the index
        // but the app rejects them, an inconsistency. EF can't model a functional
        // index, so it's created via raw SQL in the migration (and intentionally
        // not declared here). See issue #432.
    }
}
