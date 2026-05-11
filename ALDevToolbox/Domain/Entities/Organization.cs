namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A tenant boundary. Every editable entity in the system carries an
/// <c>OrganizationId</c> and queries scope to a single organisation; cross-org
/// reads are blocked by EF query filters and cross-org writes by service
/// guards (see <c>.design/auth-and-audit.md</c>).
/// </summary>
public class Organization
{
    public int Id { get; set; }

    /// <summary>Display name (e.g. <c>Acme</c>). Free text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-safe identifier the signup form matches against
    /// (<c>^[a-z0-9-]+$</c>). Unique across the deployment.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Pending organisations exist while their first signup is awaiting
    /// approval. They have no users until the bootstrap admin (or another
    /// admin in another org, in a future flow) approves the signup.
    /// </summary>
    public bool IsPending { get; set; }

    /// <summary>
    /// True for the singleton "system" organisation that hosts the canonical
    /// templates, modules and application versions other orgs fork from via
    /// <see cref="Services.TemplateImportService"/>. The Default org is
    /// stamped <c>IsSystem = true</c> by the migration; the partial unique
    /// index on this column refuses a second system org. Regular orgs start
    /// empty and pull from the system catalogue on demand.
    /// </summary>
    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }
}
