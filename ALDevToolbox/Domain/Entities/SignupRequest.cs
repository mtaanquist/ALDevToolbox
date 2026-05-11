namespace ALDevToolbox.Domain.Entities;

/// <summary>Outcome an admin recorded against a signup request.</summary>
public enum SignupDecision
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// A pending account waiting for an organisation admin to approve. The
/// matching <see cref="User"/> row is created up-front in <c>Pending</c>
/// status; this record is the workflow envelope around the approval action.
/// </summary>
public class SignupRequest
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The pending user this request created. <c>null</c> after a rejection —
    /// the user row is deleted so they can re-sign-up, but the request stays
    /// for the audit trail.
    /// </summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    public string Email { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public int? DecidedByUserId { get; set; }
    public SignupDecision Decision { get; set; }
}
