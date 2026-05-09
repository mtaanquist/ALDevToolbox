using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ALDevToolbox.Data;

/// <summary>
/// The single EF Core context for the application. Holds the database sets and
/// configures the table layout described in <c>.design/domain-model.md</c>:
/// snake_case column names, JSON-text value objects, soft-delete columns and
/// the audit log.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<RuntimeTemplate> RuntimeTemplates => Set<RuntimeTemplate>();
    public DbSet<TemplateFolder> TemplateFolders => Set<TemplateFolder>();
    public DbSet<TemplateFile> TemplateFiles => Set<TemplateFile>();
    public DbSet<RuntimeTemplateDefaultModule> RuntimeTemplateDefaultModules => Set<RuntimeTemplateDefaultModule>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<WellKnownDependency> WellKnownDependencies => Set<WellKnownDependency>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Reusable converters for the two JSON columns. Options come from
        // PersistenceJson so the round-trip stays symmetric with the audit
        // interceptor and the admin form's JSON pre-parse.
        var jsonOptions = PersistenceJson.Options;
        var defaultsConverter = new ValueConverter<TemplateDefaults, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<TemplateDefaults>(v, jsonOptions) ?? new TemplateDefaults());
        var appSourceCopConverter = new ValueConverter<AppSourceCopSettings, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<AppSourceCopSettings>(v, jsonOptions) ?? new AppSourceCopSettings());

        modelBuilder.Entity<RuntimeTemplate>(entity =>
        {
            entity.ToTable("runtime_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Key).HasColumnName("key").IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Runtime).HasColumnName("runtime").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DefaultApplication).HasColumnName("default_application").IsRequired();
            entity.Property(e => e.DefaultPlatform).HasColumnName("default_platform").IsRequired();
            entity.Property(e => e.Defaults)
                .HasColumnName("defaults_json")
                .HasConversion(defaultsConverter)
                .IsRequired();
            entity.Property(e => e.AppSourceCop)
                .HasColumnName("app_source_cop_json")
                .HasConversion(appSourceCopConverter)
                .IsRequired();
            entity.Property(e => e.CoreIdRangeFrom).HasColumnName("core_id_range_from").IsRequired();
            entity.Property(e => e.CoreIdRangeTo).HasColumnName("core_id_range_to").IsRequired();
            entity.Property(e => e.ModuleIdRangeStart).HasColumnName("module_id_range_start").IsRequired();
            entity.Property(e => e.ModuleIdRangeSize).HasColumnName("module_id_range_size").IsRequired();
            entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

            entity.HasMany(e => e.Folders)
                .WithOne(f => f.Template!)
                .HasForeignKey(f => f.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.DefaultModules)
                .WithOne(d => d.Template!)
                .HasForeignKey(d => d.RuntimeTemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RuntimeTemplateDefaultModule>(entity =>
        {
            entity.ToTable("runtime_template_default_modules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RuntimeTemplateId).HasColumnName("runtime_template_id").IsRequired();
            entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.HasIndex(e => new { e.RuntimeTemplateId, e.Ordering });
            entity.HasIndex(e => new { e.RuntimeTemplateId, e.ModuleId }).IsUnique();

            entity.HasOne(e => e.Module!)
                .WithMany()
                .HasForeignKey(e => e.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TemplateFolder>(entity =>
        {
            entity.ToTable("template_folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.TemplateId).HasColumnName("template_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.HasIndex(e => new { e.TemplateId, e.Ordering });

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Folder!)
                .HasForeignKey(f => f.TemplateFolderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TemplateFile>(entity =>
        {
            entity.ToTable("template_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.TemplateFolderId).HasColumnName("template_folder_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.HasIndex(e => new { e.TemplateFolderId, e.Ordering });
            entity.HasIndex(e => new { e.TemplateFolderId, e.Path }).IsUnique();
        });

        modelBuilder.Entity<Module>(entity =>
        {
            entity.ToTable("modules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Key).HasColumnName("key").IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.IdRangeSize).HasColumnName("id_range_size");
            entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

            entity.HasMany(e => e.Dependencies)
                .WithOne(d => d.Module!)
                .HasForeignKey(d => d.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModuleDependency>(entity =>
        {
            entity.ToTable("module_dependencies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.DepId).HasColumnName("dep_id").IsRequired();
            entity.Property(e => e.DepName).HasColumnName("dep_name").IsRequired();
            entity.Property(e => e.DepPublisher).HasColumnName("dep_publisher").IsRequired();
            entity.Property(e => e.DepVersion).HasColumnName("dep_version").IsRequired();
            entity.HasIndex(e => new { e.ModuleId, e.Ordering });
        });

        modelBuilder.Entity<WellKnownDependency>(entity =>
        {
            entity.ToTable("well_known_dependencies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.DepId).HasColumnName("dep_id").IsRequired();
            entity.Property(e => e.DepName).HasColumnName("dep_name").IsRequired();
            entity.Property(e => e.DepPublisher).HasColumnName("dep_publisher").IsRequired();
            entity.Property(e => e.DepVersionDefault).HasColumnName("dep_version_default").IsRequired();
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
            entity.Property(e => e.ChangedBy).HasColumnName("changed_by").IsRequired();
            entity.Property(e => e.EntityType)
                .HasColumnName("entity_type")
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.EntityId).HasColumnName("entity_id").IsRequired();
            entity.Property(e => e.Action)
                .HasColumnName("action")
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.SnapshotJson).HasColumnName("snapshot_json");
            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.Timestamp })
                .HasDatabaseName("ix_audit_log_entity_timestamp");
            // Standalone (timestamp) index serves the global /admin/audit
            // overview, which orders by timestamp desc with no entity filter.
            // The compound index above doesn't help that query because the
            // leading columns don't match.
            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("ix_audit_log_timestamp");
        });
    }
}
