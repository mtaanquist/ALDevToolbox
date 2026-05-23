namespace ALDevToolbox.Services;

/// <summary>
/// Static catalogue of the database tables that carry an
/// <c>organization_id</c>. Used by both <see cref="DatabaseUsageService"/>
/// (to prorate per-org disk usage) and the per-tenant backup service (to
/// know which tables to dump and restore).
///
/// New tenanted tables must be added here. The build will keep working
/// without the entry, but per-org usage and per-tenant backups will be
/// silently incomplete.
/// </summary>
internal static class TenantTableCatalog
{
    /// <summary>
    /// Tables containing tenant content — the per-tenant backup payload. In
    /// insert order (parents before children); reverse for deletes.
    /// </summary>
    public static readonly IReadOnlyList<string> ContentTables =
    [
        "application_versions",
        "well_known_dependencies",
        "runtime_templates",
        "modules",
        "runtime_template_default_modules",
        "module_dependencies",
        "module_extension_folders",
        "module_extension_files",
        "workspace_extensions",
        "workspace_extension_folders",
        "workspace_extension_files",
        "workspace_extension_dependencies",
        "organization_settings",
        "organization_assets",
        "organization_files",
        "recipes",
        "recipe_files",
        "recipe_suggestions",
        "recipe_suggestion_files",
        "oe_releases",
        "oe_modules",
        "oe_module_files",
        "oe_module_objects",
        "oe_module_symbols",
        "oe_module_variables",
        "oe_module_references",
    ];

    /// <summary>
    /// Tenanted tables that carry auth state or forensic history. Counted
    /// toward per-org disk usage so SiteAdmin sees the real footprint, but
    /// excluded from per-tenant backup/restore: replaying users or audit
    /// rows from a snapshot would tangle login state and lose evidence.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthAndAuditTables =
    [
        "users",
        "user_passkeys",
        "user_recovery_codes",
        "user_totp_secrets",
        "invites",
        "signup_requests",
        "password_reset_tokens",
        "audit_log",
    ];

    /// <summary>All tables that carry an <c>organization_id</c>.</summary>
    public static IEnumerable<string> AllTenantedTables =>
        ContentTables.Concat(AuthAndAuditTables);

    /// <summary>Tables whose rows are scoped by <c>organization_id</c> directly.</summary>
    public static readonly IReadOnlySet<string> TablesWithDirectOrgColumn = new HashSet<string>
    {
        "application_versions",
        "well_known_dependencies",
        "runtime_templates",
        "modules",
        "runtime_template_default_modules",
        "module_dependencies",
        "module_extension_folders",
        "module_extension_files",
        "workspace_extensions",
        "workspace_extension_folders",
        "workspace_extension_files",
        "workspace_extension_dependencies",
        "organization_settings",
        "organization_assets",
        "organization_files",
        "recipes",
        "recipe_files",
        "recipe_suggestions",
        "recipe_suggestion_files",
        "oe_releases",
        "oe_modules",
        "oe_module_files",
        "oe_module_objects",
        "oe_module_symbols",
        "oe_module_variables",
        "oe_module_references",
        "users",
        "invites",
        "signup_requests",
        "audit_log",
    };

    /// <summary>
    /// Auth-adjacent tables that link to <c>organization_id</c> indirectly via
    /// <c>users.id</c>. Sized by joining through <c>users</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TablesLinkedThroughUser =
        new Dictionary<string, string>
        {
            ["user_passkeys"] = "user_id",
            ["user_recovery_codes"] = "user_id",
            ["user_totp_secrets"] = "user_id",
            ["password_reset_tokens"] = "user_id",
        };
}
