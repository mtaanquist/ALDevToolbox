using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Read- and write-side service for a user's own Git repository Personal Access
/// Tokens. Tokens are scoped to <em>(user, organisation, provider)</em> and
/// encrypted with the Data Protection key ring under a per-provider purpose string
/// (mirroring the SMTP password and machine-translation key). The project-build
/// pipeline resolves the <em>triggering</em> user's token for a repo's provider, so
/// a build fails for whoever lacks access instead of leaning on a shared org
/// credential. Replaces the per-org PATs that used to live on
/// <c>organization_settings</c>. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class UserRepositoryTokenService
{
    /// <summary>Data Protection purpose string for a user's GitHub PAT.</summary>
    public const string GitHubProtectionPurpose = "ALDevToolbox.UserRepositoryToken.GitHub";

    /// <summary>Data Protection purpose string for a user's Azure DevOps PAT.</summary>
    public const string AzureDevOpsProtectionPurpose = "ALDevToolbox.UserRepositoryToken.AzureDevOps";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<UserRepositoryTokenService> _logger;
    private readonly IDataProtector _gitHubProtector;
    private readonly IDataProtector _azureDevOpsProtector;

    public UserRepositoryTokenService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<UserRepositoryTokenService> logger,
        IDataProtectionProvider protectionProvider)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
        _gitHubProtector = protectionProvider.CreateProtector(GitHubProtectionPurpose);
        _azureDevOpsProtector = protectionProvider.CreateProtector(AzureDevOpsProtectionPurpose);
    }

    private IDataProtector ProtectorFor(RepositoryProvider provider) => provider switch
    {
        RepositoryProvider.GitHub => _gitHubProtector,
        RepositoryProvider.AzureDevOps => _azureDevOpsProtector,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown repository provider."),
    };

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; UserRepositoryTokenService called outside an authenticated request.");

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; UserRepositoryTokenService called outside an authenticated request.");

    /// <summary>
    /// Audit-friendly view of the current user's stored tokens — presence and
    /// last-used only, never the ciphertext or plaintext. Keyed by provider so the
    /// account page can render a row per allowed provider.
    /// </summary>
    public async Task<IReadOnlyDictionary<RepositoryProvider, RepositoryTokenStatus>> GetTokenStatusAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var rows = await _db.UserRepositoryTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .Select(t => new { t.Provider, t.UpdatedAt, t.LastUsedAt })
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => r.Provider,
            r => new RepositoryTokenStatus(r.UpdatedAt, r.LastUsedAt));
    }

    /// <summary>True when the current user has a token stored for <paramref name="provider"/>.</summary>
    public async Task<bool> HasTokenAsync(RepositoryProvider provider, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        return await _db.UserRepositoryTokens
            .AsNoTracking()
            .AnyAsync(t => t.UserId == userId && t.Provider == provider, ct);
    }

    /// <summary>
    /// Stores or clears the current user's token for one provider. <paramref name="clear"/>
    /// removes the stored token; otherwise a non-empty <paramref name="plaintext"/> is
    /// encrypted and upserted, and an empty value leaves the stored token untouched
    /// (the same keep-on-blank semantics as the other secret forms). Only presence is
    /// ever logged.
    /// </summary>
    public async Task SaveTokenAsync(RepositoryProvider provider, string? plaintext, bool clear, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var row = await _db.UserRepositoryTokens
            .FirstOrDefaultAsync(t => t.UserId == userId && t.Provider == provider, ct);

        if (clear)
        {
            if (row is not null) _db.UserRepositoryTokens.Remove(row);
        }
        else if (!string.IsNullOrWhiteSpace(plaintext))
        {
            var cipher = ProtectorFor(provider).Protect(plaintext.Trim());
            if (row is null)
            {
                row = new UserRepositoryToken
                {
                    UserId = userId,
                    OrganizationId = orgId,
                    Provider = provider,
                    CreatedAt = now,
                };
                _db.UserRepositoryTokens.Add(row);
            }
            row.TokenEncrypted = cipher;
            row.UpdatedAt = now;
        }
        else
        {
            return; // blank with no clear flag — nothing to do
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Updated {Provider} repository token for user {UserId} (org {OrgId}, action={Action}).",
            provider.DisplayName(), userId, orgId, clear ? "cleared" : "saved");
    }

    /// <summary>
    /// Decrypts and returns the current user's token for <paramref name="provider"/>,
    /// or <see langword="null"/> when none is stored or the key ring can't decrypt it
    /// (a lost <c>app-keys</c> volume). Stamps <see cref="UserRepositoryToken.LastUsedAt"/>
    /// without tripping the audit interceptor. Consumed by the project-build pipeline,
    /// which runs under the triggering user's ambient scope.
    /// </summary>
    public async Task<string?> ResolveTokenAsync(RepositoryProvider provider, CancellationToken ct = default)
    {
        var userId = _orgContext.CurrentUserId;
        if (userId is null) return null;

        var row = await _db.UserRepositoryTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId.Value && t.Provider == provider, ct);
        if (row is null || string.IsNullOrEmpty(row.TokenEncrypted)) return null;

        string plaintext;
        try
        {
            plaintext = ProtectorFor(provider).Unprotect(row.TokenEncrypted);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt the {Provider} repository token for user {UserId}; the build will fail until it's re-entered.",
                provider.DisplayName(), userId.Value);
            return null;
        }

        // Best-effort last-used stamp; bypasses the audit interceptor.
        try
        {
            await _db.UserRepositoryTokens
                .Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stamp last-used on repository token {TokenId}.", row.Id);
        }

        return plaintext;
    }
}

/// <summary>Presence/last-used view of one stored repository token. Never carries the secret.</summary>
public sealed record RepositoryTokenStatus(DateTime UpdatedAt, DateTime? LastUsedAt);
