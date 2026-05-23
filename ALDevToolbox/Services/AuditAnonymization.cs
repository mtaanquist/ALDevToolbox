using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Detaches <c>audit_log</c> rows from the users/organisations they reference
/// so those entities can be deleted. Audit FKs are <c>Restrict</c> (#74), so a
/// referencing row must be anonymised before the delete completes; the
/// display-name string already captured in <c>changed_by</c> keeps the history
/// readable afterwards.
///
/// <para>
/// These run as raw SQL on purpose: the rows being scrubbed can belong to a
/// tenant the caller's EF query filter would hide (a SiteAdmin deleting another
/// org, or an org delete touching every member's authorship), and raw SQL
/// bypasses that filter. This is a deliberate, reviewed escape of the
/// tenant-isolation fence — keeping every such audit write here means there is
/// exactly one place to audit rather than three inline <c>ExecuteSqlRaw</c>
/// calls scattered across the account services.
/// </para>
/// </summary>
internal static class AuditAnonymization
{
    /// <summary>Clears the authorship FK on audit rows attributed to one user.</summary>
    public static Task AnonymiseActorAsync(this AppDbContext db, int userId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE audit_log SET changed_by_user_id = NULL WHERE changed_by_user_id = {0}",
            new object[] { userId }, ct);

    /// <summary>
    /// Clears the authorship FK on audit rows attributed to any user in the
    /// organisation, then detaches the org's own audit rows. Both are needed
    /// before deleting an organisation and all its members.
    /// </summary>
    public static async Task AnonymiseOrganizationAsync(this AppDbContext db, int organizationId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE audit_log SET changed_by_user_id = NULL WHERE changed_by_user_id IN (SELECT id FROM users WHERE organization_id = {0})",
            new object[] { organizationId }, ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE audit_log SET organization_id = NULL WHERE organization_id = {0}",
            new object[] { organizationId }, ct);
    }
}
