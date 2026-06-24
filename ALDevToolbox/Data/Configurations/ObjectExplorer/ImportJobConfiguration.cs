using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> entity)
    {
        entity.ToTable("oe_import_jobs");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ReleaseId).HasColumnName("release_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id");
        entity.Property(e => e.IsSiteAdmin).HasColumnName("is_site_admin").IsRequired();
        entity.Property(e => e.IsSystemOrganization).HasColumnName("is_system_organization").IsRequired();
        entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.CustomerId).HasColumnName("customer_id");
        entity.Property(e => e.DownloadUrl).HasColumnName("download_url");
        entity.Property(e => e.StagedZipPath).HasColumnName("staged_zip_path");
        entity.Property(e => e.StagedIsDvd).HasColumnName("staged_is_dvd");
        entity.Property(e => e.StoreSymbolReference).HasColumnName("store_symbol_reference").IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").IsRequired();
        entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.StartedAt).HasColumnName("started_at");
        entity.Property(e => e.CompletedAt).HasColumnName("completed_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Job row mirrors the release lifecycle 1:1; cascading the release
        // delete also reaps its job history (audit value is gone with the
        // release anyway).
        entity.HasOne<Release>()
            .WithMany()
            .HasForeignKey(e => e.ReleaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Submitter retention: if the user is deleted, keep the job audit
        // trail but null out the FK so the reconciler can still process
        // surviving in-flight rows (they re-enter the org scope via the
        // captured org-id even without a user).
        entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Reconciler scans status='queued' / 'running' at startup.
        entity.HasIndex(e => e.Status).HasDatabaseName("ix_oe_import_jobs_status");
        // Admin pages list by org, newest first.
        entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt })
            .HasDatabaseName("ix_oe_import_jobs_org_created");
    }
}
