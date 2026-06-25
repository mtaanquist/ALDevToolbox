using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectRepositoryConfiguration : IEntityTypeConfiguration<ProjectRepository>
{
    public void Configure(EntityTypeBuilder<ProjectRepository> entity)
    {
        entity.ToTable("oe_project_repositories");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
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

        // The Project -> Repositories relationship (FK project_id, cascade) is
        // configured on the Project side; here we just index the FK for the
        // per-project repo listing.
        entity.HasIndex(e => e.ProjectId)
            .HasDatabaseName("ix_oe_project_repositories_project");
    }
}
