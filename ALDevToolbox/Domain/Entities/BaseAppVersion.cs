namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One imported Microsoft application source dump, identified by the
/// (major, cumulative_update) pair. Object Explorer pages browse the
/// <see cref="BaseAppFile"/> rows that hang off this version. In BC's
/// versioning the second segment of the four-part <c>application</c> field
/// is the cumulative-update counter — there's no separate minor — so the
/// schema collapses them into one column.
/// </summary>
public class BaseAppVersion
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>BC major version, e.g. <c>28</c>.</summary>
    public int Major { get; set; }

    /// <summary>Cumulative update number. <c>0</c> means RTM.</summary>
    public int CumulativeUpdate { get; set; }

    /// <summary>
    /// Optional link to the <see cref="ApplicationVersion"/> catalogue row that
    /// matches this release wave. Nullable so admins can upload before curating
    /// a catalogue entry; doesn't affect <c>/new-workspace</c>.
    /// </summary>
    public int? ApplicationVersionId { get; set; }
    public ApplicationVersion? ApplicationVersion { get; set; }

    /// <summary>Admin-editable free-text notes (release-engineering links etc.).</summary>
    public string? Notes { get; set; }

    /// <summary>Denormalised file count so version-list rows don't COUNT(*) on every row.</summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Timestamp of the last successful symbol-index pass over this version.
    /// <c>null</c> means the version's symbols haven't been extracted yet —
    /// the <c>SymbolReindexer</c> background service picks these up and
    /// stamps the column when done. Re-imports clear it so the next pass
    /// re-extracts.
    /// </summary>
    public DateTime? SymbolsIndexedAt { get; set; }

    public DateTime UploadedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<BaseAppFile> Files { get; set; } = new List<BaseAppFile>();
}
