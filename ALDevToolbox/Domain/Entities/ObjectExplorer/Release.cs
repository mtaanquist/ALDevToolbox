namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One imported snapshot of a Business Central application surface — a DVD-style upload that
/// holds many <see cref="Module"/> rows (one per <c>.app</c> file). The Object Explorer's
/// version picker shows Releases; <c>ParentReleaseId</c> lets a third-party Release sit on
/// top of a first-party one so its references resolve up the chain. See
/// <c>.design/object-explorer.md</c> for the full model.
/// </summary>
public class Release
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>User-visible label, e.g. "BC 25.18" or "Continia DC 6.5 on BC 25.18".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// BC platform/application version stamped from a contained Module's manifest at ingest
    /// time (Base Application's <c>Application</c> field is canonical when present).
    /// Nullable because pre-ingest rows and third-party-only Releases without a Base App
    /// in the same upload may not have a clear platform version.
    /// </summary>
    public string? BcVersion { get; set; }

    /// <summary>One of <c>first_party</c>, <c>third_party</c>, <c>customer</c>.</summary>
    public string Kind { get; set; } = "first_party";

    /// <summary>
    /// Parent Release this one sits on top of. Null for first-party DVDs. Reference resolution
    /// walks the chain via recursive CTE; same-AppId modules at different versions are
    /// shadowed by the closest-to-current copy. Restricted on delete — a parent can't be
    /// removed while a child still references it.
    /// </summary>
    public int? ParentReleaseId { get; set; }
    public Release? ParentRelease { get; set; }

    /// <summary>
    /// Optional link to the <c>ApplicationVersion</c> catalogue row matching this BC release
    /// wave, for label/UI affordance. Nullable.
    /// </summary>
    public int? ApplicationVersionId { get; set; }
    public ApplicationVersion? ApplicationVersion { get; set; }

    /// <summary>
    /// One of <c>ingesting</c>, <c>ready</c>, <c>failed</c>. Releases stay hidden from the
    /// picker until <c>ready</c>; <c>failed</c> rows are tombstones SiteAdmins can clear.
    /// </summary>
    public string Status { get; set; } = "ingesting";

    /// <summary>Free-text error context for <c>status = 'failed'</c>; null otherwise.</summary>
    public string? StatusMessage { get; set; }

    public DateTime ImportedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Denormalised total <c>oe_module_files</c> count across every module in this
    /// Release, stamped when ingest flips to <c>ready</c>. Cached so the Releases
    /// picker doesn't have to fan out a correlated subquery over multi-thousand-row
    /// file tables for every page load. Updated only by import / management code —
    /// nothing else mutates the file set after a Release goes ready.
    /// </summary>
    public int SourceFileCount { get; set; }

    /// <summary>
    /// Denormalised sum of <c>LENGTH(content)</c> across this Release's source
    /// files. Counterpart to <see cref="SourceFileCount"/> for the "Size" column on
    /// the Releases picker.
    /// </summary>
    public long SourceContentLength { get; set; }

    /// <summary>Soft-delete marker. Null = active.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<Module> Modules { get; set; } = new List<Module>();
}
