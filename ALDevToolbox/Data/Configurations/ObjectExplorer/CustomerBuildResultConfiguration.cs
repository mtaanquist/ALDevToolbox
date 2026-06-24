using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class CustomerBuildResultConfiguration : IEntityTypeConfiguration<CustomerBuildResult>
{
    public void Configure(EntityTypeBuilder<CustomerBuildResult> entity)
    {
        entity.ToTable("oe_customer_build_results");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ReleaseId).HasColumnName("release_id").IsRequired();
        entity.Property(e => e.AppName).HasColumnName("app_name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").HasMaxLength(50).IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        entity.Property(e => e.Message).HasColumnName("message");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Build results mirror the release lifecycle 1:N; cascading the release
        // delete reaps its build report along with it.
        entity.HasOne(e => e.Release)
            .WithMany()
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // The manage page loads every result row for one release.
        entity.HasIndex(e => e.ReleaseId).HasDatabaseName("ix_oe_customer_build_results_release");
    }
}
