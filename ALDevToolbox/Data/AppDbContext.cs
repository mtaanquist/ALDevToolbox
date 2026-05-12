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
    /// Propagates the denormalised <c>workspace_extension_id</c> /
    /// <c>module_id</c> columns down the recursive folder tree before save.
    /// EF only sets the FK column on direct navigation children — i.e. it
    /// wires <c>extension.Folders</c> and <c>folder.ParentFolder</c>, but
    /// doesn't carry the extension's id past the first hop. The migration's
    /// data-rewrite block populates the column row-by-row; application writes
    /// have to do the same. We walk the parent chain of every added folder so
    /// that nested rows land with the right FK value, matching the unique-root
    /// and unique-sibling indexes set up in <c>OnModelCreating</c>.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PropagateExtensionFolderIds();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        PropagateExtensionFolderIds();
        return base.SaveChanges();
    }

    private void PropagateExtensionFolderIds()
    {
        foreach (var entry in ChangeTracker.Entries<WorkspaceExtensionFolder>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var folder = entry.Entity;
            if (folder.ParentFolder is null) continue;

            // Walk up to the root and copy its extension reference. The root's
            // extension nav is set by the EF parent relationship at save time,
            // so by the time SaveChanges runs the chain is well-formed.
            var root = folder.ParentFolder;
            while (root.ParentFolder is not null) root = root.ParentFolder;
            if (root.Extension is not null)
            {
                folder.Extension = root.Extension;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ModuleExtensionFolder>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var folder = entry.Entity;
            if (folder.ParentFolder is null) continue;

            var root = folder.ParentFolder;
            while (root.ParentFolder is not null) root = root.ParentFolder;
            if (root.Module is not null)
            {
                folder.Module = root.Module;
            }
        }
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
        public bool IsSiteAdmin => false;
        public bool IsSystemOrganization => false;
        public int OrganizationIdForFilter => 0;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SignupRequest> SignupRequests => Set<SignupRequest>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<RuntimeTemplate> RuntimeTemplates => Set<RuntimeTemplate>();
    public DbSet<WorkspaceExtension> WorkspaceExtensions => Set<WorkspaceExtension>();
    public DbSet<WorkspaceExtensionFolder> WorkspaceExtensionFolders => Set<WorkspaceExtensionFolder>();
    public DbSet<WorkspaceExtensionFile> WorkspaceExtensionFiles => Set<WorkspaceExtensionFile>();
    public DbSet<WorkspaceExtensionDependency> WorkspaceExtensionDependencies => Set<WorkspaceExtensionDependency>();
    public DbSet<ModuleExtensionFolder> ModuleExtensionFolders => Set<ModuleExtensionFolder>();
    public DbSet<ModuleExtensionFile> ModuleExtensionFiles => Set<ModuleExtensionFile>();
    public DbSet<RuntimeTemplateDefaultModule> RuntimeTemplateDefaultModules => Set<RuntimeTemplateDefaultModule>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<WellKnownDependency> WellKnownDependencies => Set<WellKnownDependency>();
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<OrganizationAsset> OrganizationAssets => Set<OrganizationAsset>();
    public DbSet<OrganizationFile> OrganizationFiles => Set<OrganizationFile>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<Snippet> Snippets => Set<Snippet>();
    public DbSet<SnippetFile> SnippetFiles => Set<SnippetFile>();
    public DbSet<SnippetSuggestion> SnippetSuggestions => Set<SnippetSuggestion>();
    public DbSet<SnippetSuggestionFile> SnippetSuggestionFiles => Set<SnippetSuggestionFile>();
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
            entity.Property(e => e.IsSystem).HasColumnName("is_system").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            // Partial unique index on is_system=true: at most one system org
            // exists per deployment. Regular orgs aren't subject to the
            // constraint because Postgres ignores them in the partial index.
            entity.HasIndex(e => e.IsSystem)
                .IsUnique()
                .HasFilter("is_system = true")
                .HasDatabaseName("ix_organizations_is_system_singleton");
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
            entity.Property(e => e.IsSiteAdmin).HasColumnName("is_site_admin").IsRequired();
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
            // Defense in depth: pre-login flows (Program.cs decide endpoints)
            // already call IgnoreQueryFilters explicitly; this filter catches
            // any post-login read that forgets to scope by org.
            ScopeToOrganization<SignupRequest>(entity);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.ToTable("password_reset_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
            entity.Property(e => e.Purpose).HasColumnName("purpose").HasConversion<string>().IsRequired();
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

        modelBuilder.Entity<Invite>(entity =>
        {
            entity.ToTable("invites");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Email).HasColumnName("email").IsRequired();
            entity.Property(e => e.Role).HasColumnName("role").HasConversion<string>().IsRequired();
            entity.Property(e => e.WelcomeMessage).HasColumnName("welcome_message");
            entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
            entity.Property(e => e.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.InvitedByUserId).HasColumnName("invited_by_user_id").IsRequired();
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.OrganizationId, e.Email });
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.InvitedByUser)
                .WithMany()
                .HasForeignKey(e => e.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            // Token lookups (accept-invite) bypass org filter; admin listings
            // explicitly filter by OrganizationId. Apply a query filter that
            // mirrors User's so admin-facing reads stay org-scoped.
            entity.HasQueryFilter(i => i.OrganizationId == _orgContext.OrganizationIdForFilter);
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

        modelBuilder.Entity<WorkspaceExtension>(entity =>
        {
            entity.ToTable("workspace_extensions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.TemplateId).HasColumnName("template_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.NameTemplate).HasColumnName("name_template").IsRequired();
            entity.Property(e => e.Required).HasColumnName("required").IsRequired();
            entity.Property(e => e.Application).HasColumnName("application");
            entity.Property(e => e.Runtime).HasColumnName("runtime");
            entity.Property(e => e.IdRangeFrom).HasColumnName("id_range_from");
            entity.Property(e => e.IdRangeTo).HasColumnName("id_range_to");
            entity.HasIndex(e => new { e.OrganizationId, e.TemplateId, e.Ordering });
            entity.HasIndex(e => new { e.TemplateId, e.Path }).IsUnique();

            entity.HasMany(e => e.Folders)
                .WithOne(f => f.Extension!)
                .HasForeignKey(f => f.WorkspaceExtensionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Dependencies)
                .WithOne(d => d.Extension!)
                .HasForeignKey(d => d.WorkspaceExtensionId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<WorkspaceExtension>(entity);
        });

        modelBuilder.Entity<WorkspaceExtensionFolder>(entity =>
        {
            entity.ToTable("workspace_extension_folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            // Denormalised onto every row so leaf queries don't have to walk
            // the parent chain to scope by extension. The service-layer
            // reconciliation keeps it in lock-step with the parent's value.
            entity.Property(e => e.WorkspaceExtensionId).HasColumnName("workspace_extension_id").IsRequired();
            entity.Property(e => e.ParentFolderId).HasColumnName("parent_folder_id");
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.HasIndex(e => new { e.WorkspaceExtensionId, e.ParentFolderId, e.Ordering });
            // Sibling-uniqueness: within a non-null parent, path is unique.
            entity.HasIndex(e => new { e.ParentFolderId, e.Path })
                .IsUnique()
                .HasFilter("parent_folder_id IS NOT NULL")
                .HasDatabaseName("ix_workspace_extension_folders_sibling_unique");
            // Sibling-uniqueness at the root: parent_folder_id IS NULL slice.
            entity.HasIndex(e => new { e.WorkspaceExtensionId, e.Path })
                .IsUnique()
                .HasFilter("parent_folder_id IS NULL")
                .HasDatabaseName("ix_workspace_extension_folders_root_unique");

            entity.HasOne(e => e.ParentFolder)
                .WithMany(f => f!.Folders)
                .HasForeignKey(e => e.ParentFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Folder!)
                .HasForeignKey(f => f.WorkspaceExtensionFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<WorkspaceExtensionFolder>(entity);
        });

        modelBuilder.Entity<WorkspaceExtensionFile>(entity =>
        {
            entity.ToTable("workspace_extension_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.WorkspaceExtensionFolderId).HasColumnName("workspace_extension_folder_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.IsExample).HasColumnName("is_example").IsRequired();
            entity.HasIndex(e => new { e.WorkspaceExtensionFolderId, e.Ordering });
            entity.HasIndex(e => new { e.WorkspaceExtensionFolderId, e.Path }).IsUnique();
            ScopeToOrganization<WorkspaceExtensionFile>(entity);
        });

        modelBuilder.Entity<WorkspaceExtensionDependency>(entity =>
        {
            entity.ToTable("workspace_extension_dependencies");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.WorkspaceExtensionId).HasColumnName("workspace_extension_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.RefExtensionPath).HasColumnName("ref_extension_path");
            entity.Property(e => e.RefModuleKey).HasColumnName("ref_module_key");
            entity.Property(e => e.LitId).HasColumnName("lit_id");
            entity.Property(e => e.LitName).HasColumnName("lit_name");
            entity.Property(e => e.LitPublisher).HasColumnName("lit_publisher");
            entity.Property(e => e.LitVersion).HasColumnName("lit_version");
            entity.HasIndex(e => new { e.WorkspaceExtensionId, e.Ordering });
            // Exactly one of the three reference groups must be non-null. The
            // CHECK is added in the UnifyExtensions migration; EF doesn't have
            // a fluent API for table CHECKs, so the constraint lives in raw
            // SQL there and is regenerated by future migrations via
            // AppDbContextModelSnapshot.
            ScopeToOrganization<WorkspaceExtensionDependency>(entity);
        });

        modelBuilder.Entity<ModuleExtensionFolder>(entity =>
        {
            entity.ToTable("module_extension_folders");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.ModuleId).HasColumnName("module_id").IsRequired();
            entity.Property(e => e.ParentFolderId).HasColumnName("parent_folder_id");
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.HasIndex(e => new { e.ModuleId, e.ParentFolderId, e.Ordering });
            entity.HasIndex(e => new { e.ParentFolderId, e.Path })
                .IsUnique()
                .HasFilter("parent_folder_id IS NOT NULL")
                .HasDatabaseName("ix_module_extension_folders_sibling_unique");
            entity.HasIndex(e => new { e.ModuleId, e.Path })
                .IsUnique()
                .HasFilter("parent_folder_id IS NULL")
                .HasDatabaseName("ix_module_extension_folders_root_unique");

            entity.HasOne(e => e.ParentFolder)
                .WithMany(f => f!.Folders)
                .HasForeignKey(e => e.ParentFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Files)
                .WithOne(f => f.Folder!)
                .HasForeignKey(f => f.ModuleExtensionFolderId)
                .OnDelete(DeleteBehavior.Cascade);

            ScopeToOrganization<ModuleExtensionFolder>(entity);
        });

        modelBuilder.Entity<ModuleExtensionFile>(entity =>
        {
            entity.ToTable("module_extension_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.ModuleExtensionFolderId).HasColumnName("module_extension_folder_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.Path).HasColumnName("path").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.IsExample).HasColumnName("is_example").IsRequired();
            entity.HasIndex(e => new { e.ModuleExtensionFolderId, e.Ordering });
            entity.HasIndex(e => new { e.ModuleExtensionFolderId, e.Path }).IsUnique();
            ScopeToOrganization<ModuleExtensionFile>(entity);
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

            entity.HasMany(e => e.ExtensionFolders)
                .WithOne(f => f.Module!)
                .HasForeignKey(f => f.ModuleId)
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

        modelBuilder.Entity<SystemSettings>(entity =>
        {
            entity.ToTable("system_settings");
            entity.HasKey(e => e.Id);
            // The singleton row's id is pinned to 1 by the migration.
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.SmtpHost).HasColumnName("smtp_host");
            entity.Property(e => e.SmtpPort).HasColumnName("smtp_port");
            entity.Property(e => e.SmtpUser).HasColumnName("smtp_user");
            entity.Property(e => e.SmtpPasswordEncrypted).HasColumnName("smtp_password_encrypted");
            entity.Property(e => e.SmtpFrom).HasColumnName("smtp_from");
            entity.Property(e => e.SmtpUseStartTls).HasColumnName("smtp_use_starttls");
            entity.Property(e => e.BannerText).HasColumnName("banner_text");
            entity.Property(e => e.DefaultSignupAutoApprove).HasColumnName("default_signup_auto_approve").IsRequired();
            entity.Property(e => e.BackupScheduleEnabled).HasColumnName("backup_schedule_enabled").IsRequired();
            entity.Property(e => e.BackupScheduleTimeUtc).HasColumnName("backup_schedule_time_utc")
                .HasColumnType("time without time zone").IsRequired();
            entity.Property(e => e.BackupRetentionCount).HasColumnName("backup_retention_count").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            // Cross-org table: no organization_id and no scoping query filter;
            // SiteAdminService gates mutations.
        });

        modelBuilder.Entity<Backup>(entity =>
        {
            entity.ToTable("backups");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
            entity.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().IsRequired();
            entity.Property(e => e.IsPinned).HasColumnName("is_pinned").IsRequired();
            entity.HasIndex(e => e.FileName).IsUnique();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            // Cross-org table — SiteAdminService gates mutations.
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

        modelBuilder.Entity<Snippet>(entity =>
        {
            entity.ToTable("snippets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.Title).HasColumnName("title").IsRequired();
            entity.Property(e => e.Description).HasColumnName("description").IsRequired();
            entity.Property(e => e.Keywords).HasColumnName("keywords").IsRequired();
            entity.Property(e => e.Deprecated).HasColumnName("deprecated").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.HasIndex(e => new { e.OrganizationId, e.Title }).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Files)
                .WithOne(f => f.Snippet!)
                .HasForeignKey(f => f.SnippetId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<Snippet>(entity);
        });

        modelBuilder.Entity<SnippetFile>(entity =>
        {
            entity.ToTable("snippet_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.SnippetId).HasColumnName("snippet_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.SnippetId, e.Ordering });
            ScopeToOrganization<SnippetFile>(entity);
        });

        modelBuilder.Entity<SnippetSuggestion>(entity =>
        {
            entity.ToTable("snippet_suggestions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.SuggestedByUserId).HasColumnName("suggested_by_user_id");
            entity.Property(e => e.Title).HasColumnName("title").IsRequired();
            entity.Property(e => e.Description).HasColumnName("description").IsRequired();
            entity.Property(e => e.Keywords).HasColumnName("keywords").IsRequired();
            entity.Property(e => e.Decision).HasColumnName("decision").HasConversion<string>().IsRequired();
            entity.Property(e => e.RequestedAt).HasColumnName("requested_at").IsRequired();
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.DecidedByUserId).HasColumnName("decided_by_user_id");
            entity.Property(e => e.DecisionNote).HasColumnName("decision_note");
            entity.Property(e => e.ApprovedSnippetId).HasColumnName("approved_snippet_id");
            entity.HasIndex(e => new { e.OrganizationId, e.Decision });
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.SuggestedByUser)
                .WithMany()
                .HasForeignKey(e => e.SuggestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.DecidedByUser)
                .WithMany()
                .HasForeignKey(e => e.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.ApprovedSnippet)
                .WithMany()
                .HasForeignKey(e => e.ApprovedSnippetId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(e => e.Files)
                .WithOne(f => f.Suggestion!)
                .HasForeignKey(f => f.SnippetSuggestionId)
                .OnDelete(DeleteBehavior.Cascade);
            ScopeToOrganization<SnippetSuggestion>(entity);
        });

        modelBuilder.Entity<SnippetSuggestionFile>(entity =>
        {
            entity.ToTable("snippet_suggestion_files");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
            entity.Property(e => e.SnippetSuggestionId).HasColumnName("snippet_suggestion_id").IsRequired();
            entity.Property(e => e.Ordering).HasColumnName("ordering").IsRequired();
            entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.HasIndex(e => new { e.OrganizationId, e.SnippetSuggestionId, e.Ordering });
            ScopeToOrganization<SnippetSuggestionFile>(entity);
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
