namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// An email domain claimed by an organisation. When a signup arrives with a
/// matching email address, <see cref="Services.AccountService.SignupAsync"/>
/// routes the new user into the claiming organisation as a Pending user
/// instead of having them type a slug. Domains are globally unique — the
/// admin who adds <c>acme.com</c> to one org blocks any other org from
/// claiming it, so a domain match unambiguously identifies the destination.
/// </summary>
public class OrganizationEmailDomain
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Lowercased domain (e.g. <c>acme.com</c>). No leading <c>@</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
