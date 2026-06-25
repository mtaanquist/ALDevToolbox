namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A customer whose Business Central solution the Object Explorer compiles from
/// source. Groups one or more <see cref="CustomerRepository"/> rows (Azure DevOps
/// or GitHub) that the customer-build pipeline clones, compiles, and ingests as a
/// <c>customer</c>-kind <see cref="Release"/>. Org-scoped and soft-deletable, like
/// the rest of the Object Explorer admin surface. See
/// <c>.design/object-explorer-customer-builds.md</c>.
/// </summary>
public class Customer
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// Customer-facing label used to build the Release label
    /// (<c>"{Name} on BC {Major}.{Minor}"</c>). Unique per org among active rows.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional per-customer BC localisation/country override for symbol
    /// resolution (e.g. <c>dk</c>). When null the build falls back to the org
    /// default and then <c>w1</c>. See "Symbol resolution" in the design doc.
    /// </summary>
    public string? DefaultArtifactCountry { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from the admin list unless restored.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<CustomerRepository> Repositories { get; set; } = new List<CustomerRepository>();

    /// <summary>
    /// Operator-supplied third-party symbols (<see cref="CustomerSymbol"/>) the build
    /// merges into the symbol cache — the manual-symbols recovery path for a
    /// dependency absent from both the repos' <c>.alpackages/</c> and any Microsoft
    /// artifact. See <c>.design/object-explorer-customer-builds.md</c>.
    /// </summary>
    public ICollection<CustomerSymbol> Symbols { get; set; } = new List<CustomerSymbol>();
}
