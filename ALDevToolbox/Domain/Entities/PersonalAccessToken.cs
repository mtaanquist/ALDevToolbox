namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Bearer credential issued to a user for non-interactive callers — primarily
/// the MCP server (P5/MCP milestone). The plain-text token is shown exactly
/// once at issuance and is never persisted; only its hex-encoded SHA-256
/// hash lands in <see cref="TokenHash"/>, so a database snapshot does not
/// yield usable credentials.
///
/// Tokens are <em>(user, organisation)</em>-scoped: the
/// <see cref="Services.Account.PatAuthenticationHandler"/> mounts the same
/// claims the cookie handler does, so <see cref="Services.IOrganizationContext"/>
/// and every EF query filter resolve identically to a browser sign-in.
/// </summary>
public class PersonalAccessToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>User-supplied label, e.g. "Cursor on laptop". Not unique.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 of the plain-text token. Indexed unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// First 12 chars of the plain-text token (including the <c>aldt_pat_</c>
    /// literal). Non-secret — used in the UI to disambiguate tokens when the
    /// user has several, and in audit logs to identify which token acted.
    /// </summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Reserved for a future scope split. v1 stamps <c>"mcp"</c> on every row;
    /// validation is a no-op until the column has more than one possible value.
    /// </summary>
    public string? Scopes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last validated use. Refreshed via <c>ExecuteUpdateAsync</c> so it
    /// bypasses the audit interceptor; throttled to once per minute per token
    /// to avoid write amplification under heavy MCP traffic.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Null means "no expiry". Tokens past this point fail validation.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Stamped on revoke. Tokens with a non-null value fail validation.</summary>
    public DateTime? RevokedAt { get; set; }
}
