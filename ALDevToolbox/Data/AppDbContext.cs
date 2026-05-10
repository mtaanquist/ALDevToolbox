using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ALDevToolbox.Data;

/// <summary>
/// The single EF Core context for the application. Holds the database sets and
/// configures the table layout described in <c>.design/domain-model.md</c>:
/// snake_case column names, JSON-text value objects, soft-delete columns and
/// the audit log.
///
/// Multi-tenant scoping (Milestone 13): every editable table carries an
/// <c>organization_id</c>. <see cref="OnModelCreating"/> installs query filters
/// that narrow reads to <see cref="IOrganizationContext.CurrentOrganizationId"/>;
/// pre-login flows (login, signup, bootstrap, seed) bypass with
/// <c>IgnoreQueryFilters()</c>.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly IOrganizationContext _orgContext;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        _orgContext = NullOrganizationContext.Instance;
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, IOrganizationContext orgContext) : base(options)
    {
        _orgContext = orgContext;
    }

    /// <summary>
    /// Sentinel <see cref="IOrganizationContext"/> used when the context is
    /// constructed without one (design-time tooling). Filters never match.
    /// </summary>
    private sealed class NullOrganizationContext : IOrganizationContext
    {
        public static readonly NullOrganizationContext Instance = new();
        public int? CurrentOrganizationId => null;
        public int? CurrentUserId => null;
        public int OrganizationIdForFilter => 0;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SignupRequest> SignupRequests => Set<SignupRequest>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<RuntimeTemplate> RuntimeTemplates => Set<RuntimeTemplate>();
    public DbSet<TemplateFolder> TemplateFolders => Set<TemplateFolder>();
    public DbSet<TemplateFile> TemplateFiles => Set<TemplateFile>();
    public DbSet<TemplateModuleFolder> TemplateModuleFolders => Set<TemplateModuleFolder>();
    public DbSet<TemplateModuleFile> TemplateModuleFiles => Set<TemplateModuleFile>();
    public DbSet<RuntimeTemplateDefaultModule> RuntimeTemplateDefaultModules => Set<RuntimeTemplateDefaultModule>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<WellKnownDependency> WellKnownDependencies => Set<WellKnownDependency>();
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<OrganizationAsset> OrganizationAssets => Set<OrganizationAsset>();
    public DbSet<OrganizationFile> OrganizationFiles => Set<OrganizationFile>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jsonOptions = PersistenceJson.Options;
        var defaultsConverter = new ValueConverter<TemplateDefaults, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<TemplateDefaults>(v, jsonOptions) ?? new TemplateDefaults());
        var appSourceCopConverter = new ValueConverter<AppSourceCopSettings, string>(
            v => JsonSerializer.Serialize(v, jsonOptions),
            v => JsonSerializer.Deserialize<AppSourceCopSettings>(v, jsonOptions) ?? new AppSourceCopSettings());

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Slug).HasColumnName("slug").IsRequired();
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.IsPending).HasColumnName("is_pending").IsRequired();
            entity.Property(e => e.IsSeeded).HasColumnName("is_seeded").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Email).HasColumnName("email").IsRequired();
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
            entity.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired();
            entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.HasIndex(e => new { e.OrganizationId, e.Email }).IsUnique();
            entity.HasIndex(e => e.Email);
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<User>(entity);
        });

        modelBuilder.Entity<SignupRequest>(entity =>
        {
            entity.ToTable("signup_requests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Email).HasColumnName("email").IsRequired();
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.DecidedByUserId).HasColumnName("decided_by_user_id");
            entity.Property(e => e.Decision).HasColumnName("decision").HasConversion<string>().IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.Decision });
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Mirror the User principal's org filter so EF's required-nav
            // model-validation passes; pre-login flows already bypass with
            // IgnoreQueryFilters().
            entity.HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        });

        modelBuilder.Entity<LoginAttempt>(entity =>
        {
            entity.ToTable("login_attempts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Email).HasColumnName("email").IsRequired();
            entity.Property(e => e.Ip).HasColumnName("ip").IsRequired();
            entity.Property(e => e.Succeeded).HasColumnName("succeeded").IsRequired();
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
            entity.HasIndex(e => new { e.Email, e.Timestamp });
            entity.HasIndex(e => new { e.Ip, e.Timestamp });
        });

        modelBuilder.Entity<RuntimeTemplate>(entity =>
        {
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
            entity.Property(e => e.DefaultApplication).HasColumnName("default_application").IsRequired();
            entity.Property(e => e.DefaultPlatform).HasColumnName("default_platform").IsRequired();
            entity.Property(e => e.DefaultApplicationVersionId).HasColumnName("default_application_version_id");
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
            entity.Property(e => e.CoreIdRangeFrom).HasColumnName("core_id_range_from").IsRequired();
            entity.Property(e => e.CoreIdRangeTo).HasColumnName("core_id_range_to").IsRequired();
            entity.Property(e => e.ModuleIdRangeStart).HasColumnName("module_id_range_start").IsRequired();
            entity.Property(e => e.ModuleIdRangeSize).HasColumnName("module_id_range_size").IsRequired();
            entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Folders)
                .WithOne(f => f.Template!)
                .HasForeignKey(f => f.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ModuleFolders)
                .WithOne(f => f.Template!)
                .HasForeignKey(f => f.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.DefaultModules)
                .WithOne(d => d.Template!)
                .HasForeignKey(d => d.RuntimeTemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.DefaultApplicationVersion)
                .WithMany()
                .HasForeignKey(e => e.DefaultApplicationVersionId)
                .OnDelete(DeleteBehavior.SetNull);

            ScopeToOrganization<RuntimeTemplate>(entity);
        });

        modelBuilder.Entity<ApplicationVersion>(entity =>
        {
            entity.ToTable("application_versions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Key).HasColumnName("key").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.Key }).IsUnique();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Application).HasColumnName("application").IsRequired();
            entity.Property(e => e.Runtime).HasColumnName("runtime").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<ApplicationVersion>(entity);
        });

        modelBuilder.Entity<RuntimeTemplateDefaultModule>(entity =>
        {
            entity.ToTable("runtime_template_default_modules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.RuntimeTemplateId).HasColumnName("runtime_template_id").IsRequired();
            entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.RuntimeTemplateId, e.Ordering });
            entity.HasIndex(e => new { e.RuntimeTemplateId, e.ModuleId }).IsUnique();

            entity.HasOne(e => e.Module!)
                .WithMany()
                .HasForeignKey(e => e.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<RuntimeTemplateDefaultModule>(entity);
        });

        modelBuilder.Entity<TemplateFolder>(entity =>
        {
            entity.ToTable("template_folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.TemplateId).HasColumnName("template_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.TemplateId, e.Ordering });

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Folder!)
                .HasForeignKey(f => f.TemplateFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<TemplateFolder>(entity);
        });

        modelBuilder.Entity<TemplateModuleFolder>(entity =>
        {
            entity.ToTable("template_module_folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.TemplateId).HasColumnName("template_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.TemplateId, e.Ordering });

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Folder!)
                .HasForeignKey(f => f.TemplateModuleFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<TemplateModuleFolder>(entity);
        });

        modelBuilder.Entity<TemplateModuleFile>(entity =>
        {
            entity.ToTable("template_module_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.TemplateModuleFolderId).HasColumnName("template_module_folder_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.TemplateModuleFolderId, e.Ordering });
            entity.HasIndex(e => new { e.TemplateModuleFolderId, e.Path }).IsUnique();
            ScopeToOrganization<TemplateModuleFile>(entity);
        });

        modelBuilder.Entity<TemplateFile>(entity =>
        {
            entity.ToTable("template_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.TemplateFolderId).HasColumnName("template_folder_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.TemplateFolderId, e.Ordering });
            entity.HasIndex(e => new { e.TemplateFolderId, e.Path }).IsUnique();
            ScopeToOrganization<TemplateFile>(entity);
        });

        modelBuilder.Entity<Module>(entity =>
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

            ScopeToOrganization<Module>(entity);
        });

        modelBuilder.Entity<ModuleDependency>(entity =>
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
            ScopeToOrganization<ModuleDependency>(entity);
        });

        modelBuilder.Entity<WellKnownDependency>(entity =>
        {
            entity.ToTable("well_known_dependencies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.DepId).HasColumnName("dep_id").IsRequired();
            entity.Property(e => e.DepName).HasColumnName("dep_name").IsRequired();
            entity.Property(e => e.DepPublisher).HasColumnName("dep_publisher").IsRequired();
            entity.Property(e => e.DepVersionDefault).HasColumnName("dep_version_default").IsRequired();
            entity.Property(e => e.Category).HasColumnName("category");
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.OrganizationId, e.Ordering });
            ScopeToOrganization<WellKnownDependency>(entity);
        });

        modelBuilder.Entity<OrganizationSettings>(entity =>
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
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => e.OrganizationId).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<OrganizationSettings>(entity);
        });

        modelBuilder.Entity<OrganizationAsset>(entity =>
        {
            entity.ToTable("organization_assets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
            entity.Property(e => e.ContentType).HasColumnName("content_type").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.Kind }).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<OrganizationAsset>(entity);
        });

        modelBuilder.Entity<OrganizationFile>(entity =>
        {
            entity.ToTable("organization_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.MustacheEnabled).HasColumnName("mustache_enabled").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.Ordering });
            entity.HasIndex(e => new { e.OrganizationId, e.Path }).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<OrganizationFile>(entity);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
            entity.Property(e => e.ChangedBy).HasColumnName("changed_by").IsRequired();
            entity.Property(e => e.ChangedByUserId).HasColumnName("changed_by_user_id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
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
            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("ix_audit_log_timestamp");
            entity.HasIndex(e => new { e.OrganizationId, e.Timestamp })
                .HasDatabaseName("ix_audit_log_org_timestamp");
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ChangedByUser)
                .WithMany()
                .HasForeignKey(e => e.ChangedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            // AuditLog scoping is service-layer (AuditService filters by org
            // explicitly) — we don't apply a query filter here because seed
            // and bootstrap inserts can have a null OrganizationId.
        });

        // M16: pin every DateTime column to `timestamp with time zone`. Npgsql
        // requires DateTime values to have Kind=Utc when targeting timestamptz,
        // and the codebase is already disciplined about that — every write goes
        // through DateTime.UtcNow or a `DateTimeKind.Utc` literal in the
        // builders. Pinning the column type here makes the contract explicit
        // and rules out timestamp-without-time-zone drift.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("timestamp with time zone");
                }
            }
        }
    }

    /// <summary>
    /// Installs an EF query filter that scopes reads of <typeparamref name="T"/>
    /// to the current organisation. Pre-login flows must call
    /// <c>IgnoreQueryFilters()</c> explicitly to read across organisations.
    /// </summary>
    private void ScopeToOrganization<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class
    {
        // OrganizationIdForFilter returns 0 when no user is signed in; real
        // org ids start at 1 so the comparison never matches pre-login.
        entity.HasQueryFilter(e =>
            EF.Property<int>(e, "OrganizationId") == _orgContext.OrganizationIdForFilter);
    }
}
