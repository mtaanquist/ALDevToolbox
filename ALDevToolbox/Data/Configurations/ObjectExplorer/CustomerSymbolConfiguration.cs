using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class CustomerSymbolConfiguration : IEntityTypeConfiguration<CustomerSymbol>
{
    public void Configure(EntityTypeBuilder<CustomerSymbol> entity)
    {
        entity.ToTable("oe_customer_symbols");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").HasMaxLength(260).IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.ContentLength).HasColumnName("content_length").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Customer -> Symbols relationship (FK customer_id, cascade) is
        // configured on the Customer side. Per-customer uniqueness on the file
        // name makes a re-upload of the same package a replace, not a duplicate.
        entity.HasIndex(e => new { e.CustomerId, e.FileName })
            .IsUnique()
            .HasDatabaseName("ix_oe_customer_symbols_customer_file");
    }
}
