using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    private readonly IOrganizationContext _orgContext;
    public ModuleConfiguration(IOrganizationContext orgContext) => _orgContext = orgContext;

    public void Configure(EntityTypeBuilder<Module> entity)
    {
        entity.ToTable("modules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Key).HasColumnName("key").IsRequired();
        entity.HasIndex(e => new { e.OrganizationId, e.Key }).IsUnique();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.IdRangeSize).HasColumnName("id_range_size");
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Dependencies)
            .WithOne(d => d.Module!)
            .HasForeignKey(d => d.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.ExtensionFolders)
            .WithOne(f => f.Module!)
            .HasForeignKey(f => f.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.ScopeToOrganization(_orgContext);
    }
}
