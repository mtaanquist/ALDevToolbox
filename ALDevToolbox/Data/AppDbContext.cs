using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using OeModule = ALDevToolbox.Domain.Entities.ObjectExplorer.Module;
using OeRelease = ALDevToolbox.Domain.Entities.ObjectExplorer.Release;
using OeModuleFile = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleFile;
using OeFileContent = ALDevToolbox.Domain.Entities.ObjectExplorer.FileContent;
using OeModuleObject = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleObject;
using OeModuleSymbol = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleSymbol;
using OeModuleVariable = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleVariable;
using OeModuleReference = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleReference;
using OeModuleTranslation = ALDevToolbox.Domain.Entities.ObjectExplorer.ModuleTranslation;
using OeImportJob = ALDevToolbox.Domain.Entities.ObjectExplorer.ImportJob;

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
    public DbSet<OrganizationEmailDomain> OrganizationEmailDomains => Set<OrganizationEmailDomain>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SignupRequest> SignupRequests => Set<SignupRequest>();
    // Pre-account email verification (email-first signup). Org-less and
    // user-less, so deliberately NOT scoped by the tenant query filter — read
    // via IgnoreQueryFilters() like Invites / PasswordResetTokens.
    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<UserTotpSecret> UserTotpSecrets => Set<UserTotpSecret>();
    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
    public DbSet<UserPasskey> UserPasskeys => Set<UserPasskey>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();

    public DbSet<RuntimeTemplate> RuntimeTemplates => Set<RuntimeTemplate>();
    public DbSet<WorkspaceExtension> WorkspaceExtensions => Set<WorkspaceExtension>();
    public DbSet<WorkspaceExtensionFolder> WorkspaceExtensionFolders => Set<WorkspaceExtensionFolder>();
    public DbSet<WorkspaceExtensionFile> WorkspaceExtensionFiles => Set<WorkspaceExtensionFile>();
    public DbSet<WorkspaceExtensionDependency> WorkspaceExtensionDependencies => Set<WorkspaceExtensionDependency>();
    public DbSet<ModuleExtensionFolder> ModuleExtensionFolders => Set<ModuleExtensionFolder>();
    public DbSet<ModuleExtensionFile> ModuleExtensionFiles => Set<ModuleExtensionFile>();
    public DbSet<RuntimeTemplateDefaultModule> RuntimeTemplateDefaultModules => Set<RuntimeTemplateDefaultModule>();
    public DbSet<RuntimeTemplateIncludedFile> RuntimeTemplateIncludedFiles => Set<RuntimeTemplateIncludedFile>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<WellKnownDependency> WellKnownDependencies => Set<WellKnownDependency>();
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<OrganizationAsset> OrganizationAssets => Set<OrganizationAsset>();
    public DbSet<OrganizationFile> OrganizationFiles => Set<OrganizationFile>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<PerTenantBackup> PerTenantBackups => Set<PerTenantBackup>();
    // Object Explorer (.app ingest) — see .design/object-explorer.md.
    public DbSet<OeRelease> OeReleases => Set<OeRelease>();
    public DbSet<OeModule> OeModules => Set<OeModule>();
    public DbSet<OeModuleFile> OeModuleFiles => Set<OeModuleFile>();
    public DbSet<OeFileContent> OeFileContents => Set<OeFileContent>();
    public DbSet<OeModuleObject> OeModuleObjects => Set<OeModuleObject>();
    public DbSet<OeModuleSymbol> OeModuleSymbols => Set<OeModuleSymbol>();
    public DbSet<OeModuleVariable> OeModuleVariables => Set<OeModuleVariable>();
    public DbSet<OeModuleReference> OeModuleReferences => Set<OeModuleReference>();
    public DbSet<OeModuleTranslation> OeModuleTranslations => Set<OeModuleTranslation>();
    public DbSet<OeImportJob> OeImportJobs => Set<OeImportJob>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeFile> RecipeFiles => Set<RecipeFile>();
    public DbSet<RecipeSuggestion> RecipeSuggestions => Set<RecipeSuggestion>();
    public DbSet<RecipeSuggestionFile> RecipeSuggestionFiles => Set<RecipeSuggestionFile>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>
    /// Per-user, per-organisation "trust this OAuth client" record. The
    /// OpenIddict-managed token tables (oauth_applications, _authorizations,
    /// _scopes, _tokens) are registered via <c>modelBuilder.UseOpenIddict()</c>
    /// in <see cref="OnModelCreating"/>; this is the ALDevToolbox-specific
    /// table that drives the consent screen's "already approved" auto-submit.
    /// </summary>
    public DbSet<OAuthConsent> OAuthConsents => Set<OAuthConsent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Per-entity fluent config lives in Data/Configurations/. The
        // configurations themselves are stateless — they don't install the
        // multi-tenant query filter. EF Core only re-parameterises a query
        // filter when its expression references a field/property of the
        // DbContext class itself; capturing _orgContext from a configuration
        // class would freeze the value at model-build time and leak data
        // across orgs. So filters live here, where _orgContext is "this._orgContext".
        // OpenIddict's EF Core entities. Must run BEFORE
        // ApplyConfigurationsFromAssembly so the snake_case ToTable() / column
        // overrides in Data/Configurations/OAuth/* win — UseOpenIddict() is a
        // first-write of the model, our configurations are the overrides.
        //
        // These tables are intentionally outside the multi-tenant query
        // filter: pre-auth flows (/oauth/token, /oauth/register) must read
        // them before any IOrganizationContext exists. Org attribution lives
        // in OpenIddict's free-form Properties JSON column on each row.
        modelBuilder.UseOpenIddict();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Standard tenant filter: scope every entity with an OrganizationId
        // column to the current organisation. Pre-login flows must call
        // IgnoreQueryFilters() explicitly.
        ScopeToOrganization<User>(modelBuilder);
        ScopeToOrganization<SignupRequest>(modelBuilder);
        ScopeToOrganization<Invite>(modelBuilder, i => i.OrganizationId == _orgContext.OrganizationIdForFilter);
        ScopeToOrganization<RuntimeTemplate>(modelBuilder);
        ScopeToOrganization<ApplicationVersion>(modelBuilder);
        ScopeToOrganization<RuntimeTemplateDefaultModule>(modelBuilder);
        ScopeToOrganization<RuntimeTemplateIncludedFile>(modelBuilder);
        ScopeToOrganization<WorkspaceExtension>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionFolder>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionFile>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionDependency>(modelBuilder);
        ScopeToOrganization<ModuleExtensionFolder>(modelBuilder);
        ScopeToOrganization<ModuleExtensionFile>(modelBuilder);
        ScopeToOrganization<Module>(modelBuilder);
        ScopeToOrganization<ModuleDependency>(modelBuilder);
        ScopeToOrganization<WellKnownDependency>(modelBuilder);
        ScopeToOrganization<OrganizationSettings>(modelBuilder);
        ScopeToOrganization<OrganizationAsset>(modelBuilder);
        ScopeToOrganization<OrganizationFile>(modelBuilder);
        ScopeToOrganization<OrganizationEmailDomain>(modelBuilder);
        ScopeToOrganization<OeRelease>(modelBuilder);
        ScopeToOrganization<OeModule>(modelBuilder);
        ScopeToOrganization<OeModuleFile>(modelBuilder);
        ScopeToOrganization<OeModuleObject>(modelBuilder);
        ScopeToOrganization<OeModuleSymbol>(modelBuilder);
        ScopeToOrganization<OeModuleVariable>(modelBuilder);
        ScopeToOrganization<OeModuleReference>(modelBuilder);
        ScopeToOrganization<OeModuleTranslation>(modelBuilder);
        ScopeToOrganization<OeImportJob>(modelBuilder);
        // NOTE: OeFileContent (oe_file_contents) is deliberately NOT scoped.
        // It is the content-addressable, cross-tenant-shared source-blob store;
        // it has no organization_id. Isolation holds because it is only ever
        // reached via the OeModuleFile.FileContent nav from an org-scoped file
        // row — never queried as a root. Do not add a filter here.
        ScopeToOrganization<Recipe>(modelBuilder);
        ScopeToOrganization<RecipeFile>(modelBuilder);
        ScopeToOrganization<RecipeSuggestion>(modelBuilder);
        ScopeToOrganization<RecipeSuggestionFile>(modelBuilder);
        ScopeToOrganization<PersonalAccessToken>(modelBuilder);
        ScopeToOrganization<OAuthConsent>(modelBuilder);

        // PasswordResetToken scopes via its required User principal: tokens
        // don't carry organization_id themselves, so the filter walks the nav.
        modelBuilder.Entity<PasswordResetToken>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);

        // MFA / passkey tables follow the PasswordResetToken pattern: scope via
        // the User principal. Login flows that run before the auth cookie is
        // set (TOTP / email-MFA / passkey verification) call
        // <c>IgnoreQueryFilters()</c> explicitly.
        modelBuilder.Entity<UserTotpSecret>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        modelBuilder.Entity<UserRecoveryCode>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        modelBuilder.Entity<UserPasskey>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
    }

    /// <summary>
    /// Installs the standard organization-scoped query filter on
    /// <typeparamref name="T"/>. Lives on the DbContext (not on a
    /// configuration class) so the captured <c>_orgContext</c> resolves at
    /// query time via the DbContext instance — see comment in
    /// <see cref="OnModelCreating"/>.
    /// </summary>
    private void ScopeToOrganization<T>(ModelBuilder modelBuilder) where T : class
        => modelBuilder.Entity<T>().HasQueryFilter(e =>
            EF.Property<int>(e, "OrganizationId") == _orgContext.OrganizationIdForFilter);

    private void ScopeToOrganization<T>(ModelBuilder modelBuilder, System.Linq.Expressions.Expression<Func<T, bool>> filter)
        where T : class
        => modelBuilder.Entity<T>().HasQueryFilter(filter);

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
