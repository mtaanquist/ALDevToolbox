using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class CustomerRepositoryConfiguration : IEntityTypeConfiguration<CustomerRepository>
{
    public void Configure(EntityTypeBuilder<CustomerRepository> entity)
    {
        entity.ToTable("oe_customer_repositories");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CustomerId).HasColumnName("customer_id").IsRequired();
        // Stored as the short string discriminator (azure_devops / github), not the
        // enum's ordinal, so the column reads meaningfully and is stable if the enum reorders.
        entity.Property(e => e.Provider)
            .HasColumnName("provider")
            .HasMaxLength(40)
            .IsRequired()
            .HasConversion(
                p => p.ToDiscriminator(),
                s => RepositoryProviders.FromDiscriminatorStrict(s));
        entity.Property(e => e.Url).HasColumnName("url").HasMaxLength(2000).IsRequired();
        entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Customer -> Repositories relationship (FK customer_id, cascade) is
        // configured on the Customer side; here we just index the FK for the
        // per-customer repo listing.
        entity.HasIndex(e => e.CustomerId)
            .HasDatabaseName("ix_oe_customer_repositories_customer");
    }
}
