using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettings>
{
    private readonly IOrganizationContext _orgContext;
    public OrganizationSettingsConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<OrganizationSettings> entity)
    {
        entity.ToTable("organization_settings");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.DefaultPublisher).HasColumnName("default_publisher").IsRequired();
        entity.Property(e => e.DefaultIdRangeFrom).HasColumnName("default_id_range_from").IsRequired();
        entity.Property(e => e.DefaultIdRangeTo).HasColumnName("default_id_range_to").IsRequired();
        entity.Property(e => e.DefaultBrief).HasColumnName("default_brief").IsRequired();
        entity.Property(e => e.DefaultCoreDescription).HasColumnName("default_core_description").IsRequired();
        entity.Property(e => e.CodeWorkspaceJson).HasColumnName("code_workspace_json").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasIndex(e => e.OrganizationId).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.ScopeToOrganization(_orgContext);
    }
}
