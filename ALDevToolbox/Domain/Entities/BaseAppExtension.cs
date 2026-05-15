namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One AL extension package — the metadata block from an imported ZIP's
/// <c>app.json</c>. A BC release ships each app (BaseApp, System
/// Application, the test apps, …) as its own ZIP, so each successful
/// import that contains an <c>app.json</c> produces one row here. Files
/// in the same ZIP share this row via <see cref="BaseAppFile.ExtensionId"/>;
/// imports without an <c>app.json</c> (legacy bundles, source dumps,
/// hand-zipped folders) get null on every file.
///
/// Uniqueness is on <c>(version_id, app_id)</c> — the GUID from app.json
/// — so a re-import of the same app into the same version reuses the
/// existing row rather than spawning a duplicate.
/// </summary>
public class BaseAppExtension
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int VersionId { get; set; }
    public BaseAppVersion? Version { get; set; }

    /// <summary>The <c>id</c> field from <c>app.json</c> — a stable GUID per extension.</summary>
    public Guid AppId { get; set; }

    /// <summary>The <c>name</c> field from <c>app.json</c> (e.g. "Base Application").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The <c>publisher</c> field from <c>app.json</c> (e.g. "Microsoft").</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>The <c>version</c> field from <c>app.json</c> (e.g. "28.1.123456.789").</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public ICollection<BaseAppFile> Files { get; set; } = new List<BaseAppFile>();
}
