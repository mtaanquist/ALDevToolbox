using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

// Customer modules. See .design/solution-customer-info.md, "Modules".

internal sealed class CustomerModuleConfiguration : IEntityTypeConfiguration<CustomerModule>
{
    public void Configure(EntityTypeBuilder<CustomerModule> entity)
    {
        entity.ToTable("customer_modules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.Publisher).HasColumnName("publisher").HasMaxLength(250);
        entity.Property(e => e.AppId).HasColumnName("app_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // One entry per name in an organisation; the app id, when there is one, is unique too.
        entity.HasIndex(e => new { e.OrganizationId, e.Name }).IsUnique();
        entity.HasIndex(e => new { e.OrganizationId, e.AppId }).IsUnique();

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectModuleConfiguration : IEntityTypeConfiguration<OeProjectModule>
{
    public void Configure(EntityTypeBuilder<OeProjectModule> entity)
    {
        entity.ToTable("oe_project_modules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(50);
        entity.Property(e => e.Note).HasColumnName("note").HasMaxLength(250);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasIndex(e => new { e.ProjectId, e.ModuleId }).IsUnique();
        // "Who has this module" - the Solutions list's filter - and the cascade from the catalogue.
        entity.HasIndex(e => e.ModuleId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Project!).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Module!).WithMany().HasForeignKey(e => e.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EnvironmentAppConfiguration : IEntityTypeConfiguration<OeEnvironmentApp>
{
    public void Configure(EntityTypeBuilder<OeEnvironmentApp> entity)
    {
        entity.ToTable("oe_environment_apps");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.EnvironmentId).HasColumnName("environment_id").IsRequired();
        entity.Property(e => e.AppId).HasColumnName("app_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
        entity.Property(e => e.Publisher).HasColumnName("publisher").HasMaxLength(250).IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(50).IsRequired();
        entity.Property(e => e.FetchedAt).HasColumnName("fetched_at").IsRequired();

        entity.HasIndex(e => new { e.EnvironmentId, e.AppId }).IsUnique();
        // "Who has this app installed" - the Solutions list's filter.
        entity.HasIndex(e => e.AppId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Environment!).WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
    }
}
