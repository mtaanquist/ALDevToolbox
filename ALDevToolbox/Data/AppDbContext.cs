using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Data;

/// <summary>
/// The single EF Core context for the application. Holds the database sets and
/// configures the table layout described in <c>.design/domain-model.md</c>:
/// snake_case column names, JSON-text value objects, soft-delete columns and
/// the audit log.
///
/// Per-entity fluent configuration lives in <c>Data/Configurations/</c> as
/// individual <see cref="IEntityTypeConfiguration{TEntity}"/> classes; this
/// type only wires DbSets, save-time invariants, and registers the
/// configurations.
///
/// Multi-tenant scoping (Milestone 13): every editable table carries an
/// <c>organization_id</c>. The per-entity configurations install query
/// filters that narrow reads to
/// <see cref="IOrganizationContext.CurrentOrganizationId"/>; pre-login flows
/// (login, signup, bootstrap, seed) bypass with <c>IgnoreQueryFilters()</c>.
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
    /// and unique-sibling indexes set up in the folder configurations.
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
        // Stateless configurations (no IOrganizationContext dependency) are
        // applied via assembly scan; tenant-scoped ones are instantiated with
        // the context's IOrganizationContext so each entity's query filter
        // captures the right runtime value.
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new LoginAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new SystemSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new BackupConfiguration());
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());

        modelBuilder.ApplyConfiguration(new UserConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new SignupRequestConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new PasswordResetTokenConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new InviteConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new RuntimeTemplateConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new ApplicationVersionConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new RuntimeTemplateDefaultModuleConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new WorkspaceExtensionConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new WorkspaceExtensionFolderConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new WorkspaceExtensionFileConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new WorkspaceExtensionDependencyConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new ModuleExtensionFolderConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new ModuleExtensionFileConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new ModuleConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new ModuleDependencyConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new WellKnownDependencyConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new OrganizationSettingsConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new OrganizationAssetConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new OrganizationFileConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new SnippetConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new SnippetFileConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new SnippetSuggestionConfiguration(_orgContext));
        modelBuilder.ApplyConfiguration(new SnippetSuggestionFileConfiguration(_orgContext));
    }

    /// <summary>
    /// M16: pin every <see cref="DateTime"/> column to
    /// <c>timestamp with time zone</c>. Npgsql requires <c>DateTimeKind.Utc</c>
    /// when targeting timestamptz, and the codebase already routes every
    /// write through <c>DateTime.UtcNow</c> or a UTC literal. Doing this via
    /// <see cref="ConfigureConventions"/> rather than iterating every
    /// property on every entity type keeps the model creator small (#81).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp with time zone");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("timestamp with time zone");
    }
}
