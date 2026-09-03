using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Plain reads over the consent rows in our own <c>oauth_consents</c> table.
/// Separate from <see cref="OAuthClientAdminService"/> on purpose: that service
/// joins every consent against OpenIddict's application registry to get display
/// names, which pulls the OpenIddict managers in behind it. The MCP setup page
/// only needs a yes/no, so it takes this instead.
/// </summary>
/// <remarks>
/// Reads stay inside the organisation query filter — this org's consents for
/// this user is exactly the scope wanted, so there is no
/// <c>IgnoreQueryFilters()</c> call here.
/// </remarks>
public sealed class OAuthConsentService
{
    private readonly AppDbContext _db;

    public OAuthConsentService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Whether this user has consented to any OAuth client.</summary>
    public Task<bool> HasAnyConsentAsync(int userId, CancellationToken ct = default)
        => _db.OAuthConsents
            .AsNoTracking()
            .AnyAsync(c => c.UserId == userId, ct);
}
