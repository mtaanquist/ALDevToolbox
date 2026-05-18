using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationEmailDomainConfiguration : IEntityTypeConfiguration<OrganizationEmailDomain>
{
    public void Configure(EntityTypeBuilder<OrganizationEmailDomain> entity)
    {
        entity.ToTable("organization_email_domains");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Domain).HasColumnName("domain").HasMaxLength(253).IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Global uniqueness on domain — signup routing assumes a domain
        // unambiguously identifies its claiming org.
        entity.HasIndex(e => e.Domain)
            .IsUnique()
            .HasDatabaseName("ix_organization_email_domains_domain");

        // Per-org index for the admin list page lookup.
        entity.HasIndex(e => e.OrganizationId)
            .HasDatabaseName("ix_organization_email_domains_organization_id");
    }
}
