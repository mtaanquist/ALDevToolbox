using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ModuleDependencyConfiguration : IEntityTypeConfiguration<ModuleDependency>
{
    private readonly IOrganizationContext _orgContext;
    public ModuleDependencyConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<ModuleDependency> entity)
    {
        entity.ToTable("module_dependencies");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
        entity.Property(e => e.DepId).HasColumnName("dep_id").IsRequired();
        entity.Property(e => e.DepName).HasColumnName("dep_name").IsRequired();
        entity.Property(e => e.DepPublisher).HasColumnName("dep_publisher").IsRequired();
        entity.Property(e => e.DepVersion).HasColumnName("dep_version").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.ModuleId, e.Ordering });
        entity.ScopeToOrganization(_orgContext);
    }
}
