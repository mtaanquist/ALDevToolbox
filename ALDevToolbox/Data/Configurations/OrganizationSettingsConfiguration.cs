using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettings>
{

    public void Configure(EntityTypeBuilder<OrganizationSettings> entity)
    {
        entity.ToTable("organization_settings");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.DefaultPublisher).HasColumnName("default_publisher").IsRequired();
        entity.Property(e => e.DefaultUrl).HasColumnName("default_url");
        entity.Property(e => e.DefaultLogo).HasColumnName("default_logo");
        // text[] gives us native Postgres array semantics; the value comparer
        // round-trips through a List<string> on the C# side without needing a
        // JSON value converter.
        entity.Property(e => e.DefaultSupportedCountries)
            .HasColumnName("default_supported_countries")
            .HasColumnType("text[]")
            .IsRequired();
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
    }
}
