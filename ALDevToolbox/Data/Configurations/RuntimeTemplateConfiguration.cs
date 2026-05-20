using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ALDevToolbox.Data.Configurations;

internal sealed class RuntimeTemplateConfiguration : IEntityTypeConfiguration<RuntimeTemplate>
{

    public void Configure(EntityTypeBuilder<RuntimeTemplate> entity)
    {
        var jsonOptions = PersistenceJson.Options;
        var defaultsConverter = new ValueConverter<TemplateDefaults, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<TemplateDefaults>(v, jsonOptions) ?? new TemplateDefaults());
        var appSourceCopConverter = new ValueConverter<AppSourceCopSettings, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<AppSourceCopSettings>(v, jsonOptions) ?? new AppSourceCopSettings());

        entity.ToTable("runtime_templates");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Key).HasColumnName("key").IsRequired();
        // Per-org uniqueness (M13): the template `key` is unique within an
        // organisation, not globally.
        entity.HasIndex(e => new { e.OrganizationId, e.Key }).IsUnique();
        entity.Property(e => e.Runtime).HasColumnName("runtime").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Description).HasColumnName("description");
        entity.Property(e => e.DefaultApplicationVersionId).HasColumnName("default_application_version_id");
        entity.Property(e => e.DefaultApplicationVersionLatest)
            .HasColumnName("default_application_version_latest")
            .IsRequired()
            .HasDefaultValue(false);
        // M16: jsonb. The value-converter still goes through string round-
        // trips on the C# side; HasColumnType pins the storage shape so EF
        // doesn't fall back to text. No JSONB GIN index yet — add one when
        // a query needs it, not before (see .design/milestones.md, M16).
        entity.Property(e => e.Defaults)
            .HasColumnName("defaults_json")
            .HasColumnType("jsonb")
            .HasConversion(defaultsConverter)
            .IsRequired();
        entity.Property(e => e.AppSourceCop)
            .HasColumnName("app_source_cop_json")
            .HasColumnType("jsonb")
            .HasConversion(appSourceCopConverter)
            .IsRequired();
        // Per-template overlay for the .code-workspace JSON (issue #61).
        // Nullable: most templates inherit the org-level base unchanged.
        entity.Property(e => e.CodeWorkspaceJson).HasColumnName("code_workspace_json");
        entity.Property(e => e.CoreIdRangeFrom).HasColumnName("core_id_range_from").IsRequired();
        entity.Property(e => e.CoreIdRangeTo).HasColumnName("core_id_range_to").IsRequired();
        entity.Property(e => e.ModuleIdRangeStart).HasColumnName("module_id_range_start").IsRequired();
        entity.Property(e => e.ModuleIdRangeSize).HasColumnName("module_id_range_size").IsRequired();
        entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
        entity.Property(e => e.IsDefault).HasColumnName("is_default").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        // Filtered unique index: at most one active default template per
        // organisation. The WHERE clause excludes both soft-deleted and
        // non-default rows so swapping the default is a single UPDATE.
        entity.HasIndex(e => new { e.OrganizationId, e.IsDefault })
            .IsUnique()
            .HasFilter("is_default = true AND deleted_at IS NULL");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.WorkspaceExtensions)
            .WithOne(f => f.Template!)
            .HasForeignKey(f => f.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.DefaultModules)
            .WithOne(d => d.Template!)
            .HasForeignKey(d => d.RuntimeTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.IncludedFiles)
            .WithOne(f => f.RuntimeTemplate!)
            .HasForeignKey(f => f.RuntimeTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.DefaultApplicationVersion)
            .WithMany()
            .HasForeignKey(e => e.DefaultApplicationVersionId)
            .OnDelete(DeleteBehavior.SetNull);

    }
}
