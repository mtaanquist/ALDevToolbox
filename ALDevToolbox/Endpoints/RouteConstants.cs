namespace ALDevToolbox.Endpoints;

/// <summary>
/// Centralises the redirect target URLs used by the endpoint handlers so
/// renames are a single-file change. Only the paths that recur across
/// multiple endpoints live here; one-off redirects can stay inline.
/// </summary>
internal static class RouteConstants
{
    public const string Login = "/login";
    public const string Account = "/account";
    public const string AdminUsers = "/admin/users";
    public const string AdminTemplates = "/admin/templates";
    public const string AdminExport = "/admin/export";
    public const string SiteAdminUsers = "/site-admin/users";
    public const string SiteAdminSettings = "/site-admin/settings";
    public const string SiteAdminBackups = "/site-admin/backups";
    public const string SiteAdminStorage = "/site-admin/storage";

    public const string OkQuery = "ok";
    public const string ErrQuery = "err";
    public const string MsgQuery = "msg";
}
