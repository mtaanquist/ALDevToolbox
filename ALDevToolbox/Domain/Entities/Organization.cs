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
    /// admin in another org, in a future flow) approves the signup. The org's
    /// content is seeded the first time an admin signs in.
    /// </summary>
    public bool IsPending { get; set; }

    /// <summary>
    /// True after <see cref="Services.SeedService"/> has populated this org's
    /// content. Stays false for orgs created via approved signup until an
    /// admin first signs in; then we seed and flip the flag.
    /// </summary>
    public bool IsSeeded { get; set; }

    public DateTime CreatedAt { get; set; }
}
