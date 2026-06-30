namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A reusable "where + how" of a deploy: a named release configuration that draws a
/// <see cref="Pipeline"/> (build) pipeline's artifacts and targets one
/// <see cref="ProjectEnvironment"/>. The deliberate counterpart to the build half of
/// the split — a build pipeline can feed several release pipelines, so the same build
/// is deployed to several environments (test in Sandbox, promote the identical
/// artifact to Production). Reads as <em>"Release Contoso App on Production."</em>
/// Org-scoped via the standard query filter; soft-deleted; management rights come
/// from the parent project's owner via <c>ProjectAccess</c>. Scheduling a delivery of
/// a chosen build (and the publish flow itself) lands in a later slice — this entity
/// is the config it reads. See <c>.design/saas-delivery.md</c>.
/// </summary>
public class ReleasePipeline
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) this release pipeline belongs to.</summary>
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// The user who created it — its owner of record. Nullable
    /// (<c>ON DELETE SET NULL</c>) so the release pipeline outlives the account;
    /// management rights come from the parent project's owner via <c>ProjectAccess</c>.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>Display name, unique per project among active rows (e.g. <c>Contoso App → Production</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The artifact source — releases publish this build pipeline's builds.</summary>
    public int BuildPipelineId { get; set; }
    public Pipeline? BuildPipeline { get; set; }

    /// <summary>The target environment (carries the chosen company and the Production/Sandbox type).</summary>
    public int ProjectEnvironmentId { get; set; }
    public ProjectEnvironment? ProjectEnvironment { get; set; }

    /// <summary>
    /// Which version the upload targets, mapped to the automation API's
    /// <c>extensionUpload.schedule</c>. One of <see cref="ReleaseVersionMode"/>.
    /// (The API calls it "schedule" but it's a version target, not a time.)
    /// </summary>
    public string VersionMode { get; set; } = ReleaseVersionMode.CurrentVersion;

    /// <summary>
    /// How the install reconciles table schema, mapped to the automation API's
    /// <c>schemaSyncMode</c>. One of <see cref="SchemaSyncMode"/>;
    /// <see cref="SchemaSyncMode.ForceSync"/> can drop columns and is gated behind a confirm.
    /// </summary>
    public string SchemaSyncMode { get; set; } = Entities.ObjectExplorer.SchemaSyncMode.Add;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored.</summary>
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// The version target an upload aims at, the user-facing values of the automation
/// API's <c>extensionUpload.schedule</c>. Stored verbatim so no mapping table is
/// needed when the publish flow calls the API.
/// </summary>
public static class ReleaseVersionMode
{
    /// <summary>Hot-swap the same version that's already live (the default).</summary>
    public const string CurrentVersion = "Current Version";

    /// <summary>Install as the next minor version.</summary>
    public const string NextMinorVersion = "Next Minor Version";

    /// <summary>Install as the next major version.</summary>
    public const string NextMajorVersion = "Next Major Version";

    /// <summary>The values offered in the picker, in display order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { CurrentVersion, NextMinorVersion, NextMajorVersion };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

/// <summary>
/// How an install reconciles changed table schema, the user-facing values of the
/// automation API's <c>schemaSyncMode</c>.
/// </summary>
public static class SchemaSyncMode
{
    /// <summary>Additive only — never drops a column. The safe default.</summary>
    public const string Add = "Add";

    /// <summary>Force the schema to match, which can drop columns and lose data. Gated behind a confirm.</summary>
    public const string ForceSync = "Force Sync";

    /// <summary>The values offered in the picker, in display order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Add, ForceSync };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}
