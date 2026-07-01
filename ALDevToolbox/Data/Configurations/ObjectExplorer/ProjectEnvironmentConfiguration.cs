using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectEnvironmentConfiguration : IEntityTypeConfiguration<ProjectEnvironment>
{
    public void Configure(EntityTypeBuilder<ProjectEnvironment> entity)
    {
        entity.ToTable("oe_project_environments");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
        entity.Property(e => e.CompanyId).HasColumnName("company_id");
        entity.Property(e => e.CompanyName).HasColumnName("company_name").HasMaxLength(250);
        entity.Property(e => e.FetchedAt).HasColumnName("fetched_at").IsRequired();
        entity.Property(e => e.MissingSince).HasColumnName("missing_since");
        entity.Property(e => e.UpdateWindowStart).HasColumnName("update_window_start");
        entity.Property(e => e.UpdateWindowEnd).HasColumnName("update_window_end");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Project -> Environments: cascade so a deleted project takes its fetched
        // environments with it. The release-pipeline FK to this row is Restrict
        // (configured on the ReleasePipeline side) so a customer-deleted
        // environment is stamped MissingSince rather than removed while referenced.
        entity.HasOne(e => e.Project)
            .WithMany(p => p.Environments)
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The refresh upsert matches on (project_id, name); a unique index keeps
        // it one row per environment per project and backs the lookup.
        entity.HasIndex(e => new { e.ProjectId, e.Name })
            .IsUnique()
            .HasDatabaseName("ix_oe_project_environments_project_name");
    }
}
