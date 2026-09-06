using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectBuildDiagnosticConfiguration : IEntityTypeConfiguration<OeProjectBuildDiagnostic>
{
    public void Configure(EntityTypeBuilder<OeProjectBuildDiagnostic> entity)
    {
        entity.ToTable("oe_project_build_diagnostics");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectBuildId).HasColumnName("project_build_id").IsRequired();
        entity.Property(e => e.ProjectRepositoryId).HasColumnName("project_repository_id");
        entity.Property(e => e.Path).HasColumnName("path").HasMaxLength(1000).IsRequired();
        entity.Property(e => e.Line).HasColumnName("line").IsRequired();
        entity.Property(e => e.Column).HasColumnName("column").IsRequired();
        entity.Property(e => e.Severity).HasColumnName("severity").HasMaxLength(20).IsRequired();
        entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        entity.Property(e => e.Message).HasColumnName("message").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The build relationship is configured from ProjectBuild
        // (HasMany(e => e.Diagnostics)); don't redeclare it.

        entity.HasOne(e => e.ProjectRepository)
            .WithMany()
            .HasForeignKey(e => e.ProjectRepositoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Every read is "this build's diagnostics, in the compiler's order" - the
        // count on the build page and the annotation batch for the check run.
        entity.HasIndex(e => new { e.ProjectBuildId, e.Ordering })
            .HasDatabaseName("ix_oe_project_build_diagnostics_build_ordering");
    }
}
