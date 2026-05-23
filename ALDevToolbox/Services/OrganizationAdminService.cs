using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Per-organisation administrative toggles and the email-domain allow-list.
/// Split out of <see cref="OrganizationConfigService"/> (which now sticks to
/// reading/writing the generation config) so the dependencies these actions
/// need — <see cref="IMcpAvailability"/> for the MCP opt-out and
/// <see cref="AuthService"/> for the strong-auth self-check — don't have to be
/// dragged through the config-read path (and the unauthenticated signup flow
/// that touches it). Delegates cache invalidation back to
/// <see cref="OrganizationConfigService.InvalidateCache"/>.
/// </summary>
public sealed class OrganizationAdminService
{
    private static readonly Regex DomainRegex = new(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly IMcpAvailability _mcpAvailability;
    private readonly AuthService _auth;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<OrganizationAdminService> _logger;

    public OrganizationAdminService(
        AppDbContext db,
        IOrganizationContext orgContext,
        IMcpAvailability mcpAvailability,
        AuthService auth,
        OrganizationConfigService config,
        ILogger<OrganizationAdminService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _mcpAvailability = mcpAvailability;
        _auth = auth;
        _config = config;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>Lists the email domains claimed by the current organisation.</summary>
    public Task<List<OrganizationEmailDomain>> ListEmailDomainsAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        return _db.OrganizationEmailDomains
            .AsNoTracking()
            .Where(d => d.OrganizationId == orgId)
            .OrderBy(d => d.Domain)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Claims a new email domain for the current organisation. Domains are
    /// globally unique so a successful add blocks every other org from
    /// claiming the same domain — a friendly error surfaces if it's already
    /// taken (whether by this org or another).
    /// </summary>
    public async Task AddEmailDomainAsync(string domain, CancellationToken ct = default)
    {
        var normalised = NormaliseDomain(domain);
        if (!DomainRegex.IsMatch(normalised) || normalised.Length > 253)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = "Enter a valid domain like 'acme.com'.",
            });
        }

        var orgId = RequireOrganizationId();
        var existing = await _db.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Domain == normalised, ct);
        if (existing is not null)
        {
            var msg = existing.OrganizationId == orgId
                ? "That domain is already on the list."
                : "That domain is claimed by another organisation.";
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = msg,
            });
        }

        _db.OrganizationEmailDomains.Add(new OrganizationEmailDomain
        {
            OrganizationId = orgId,
            Domain = normalised,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} claimed email domain {Domain}.", orgId, normalised);
    }

    /// <summary>Removes one of the current organisation's email-domain claims.</summary>
    public async Task RemoveEmailDomainAsync(int domainId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        // Query filter scopes to the current org, so cross-org IDs return null.
        var row = await _db.OrganizationEmailDomains.FirstOrDefaultAsync(d => d.Id == domainId, ct);
        if (row is null) return;
        _db.OrganizationEmailDomains.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} released email domain {Domain}.", orgId, row.Domain);
    }

    /// <summary>
    /// Toggles the per-org MCP opt-out. Refuses when the site-wide
    /// <see cref="IMcpAvailability"/> is off — the UI also disables the
    /// checkbox in that case but the service enforces it independently so a
    /// forged POST can't flip a hidden setting.
    /// </summary>
    public async Task SetMcpEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!_mcpAvailability.IsEnabled)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["McpEnabled"] = "MCP is disabled site-wide. Ask a site admin to enable it first.",
            });
        }
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (org.McpEnabled == enabled) return;
        org.McpEnabled = enabled;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} set MCP enabled = {Enabled}.", orgId, enabled);
    }

    /// <summary>
    /// Flips the per-org strong-auth requirement. The toggling admin must
    /// themselves satisfy the requirement when turning it on, so an admin
    /// can't accidentally lock themselves out by saving the form before
    /// enrolling a TOTP / passkey. Disabling is unconditional.
    /// </summary>
    public async Task SetRequireStrongAuthAsync(bool enabled, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = _orgContext.CurrentUserId
            ?? throw new InvalidOperationException("No user in scope; SetRequireStrongAuthAsync called outside an authenticated request.");

        if (enabled && !await _auth.HasStrongAuthAsync(userId, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["RequireStrongAuth"] = "Set up an authenticator app, email codes or a passkey on your own account before turning this on — otherwise your next request would lock you out of the admin tools.",
            });
        }

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        if (row.RequireStrongAuth == enabled) return;
        row.RequireStrongAuth = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation("Org {OrgId} set require_strong_auth = {Enabled}.", orgId, enabled);
    }

    private static string NormaliseDomain(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed.StartsWith('@')) trimmed = trimmed[1..];
        return trimmed;
    }
}
