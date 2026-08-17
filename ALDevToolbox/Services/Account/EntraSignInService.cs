using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>The identity claims we consume from a validated Entra id_token.</summary>
/// <param name="TenantId">The <c>tid</c> claim — the Entra tenant the account lives in.</param>
/// <param name="ObjectId">The <c>oid</c> claim — the stable per-tenant account id we key links on.</param>
/// <param name="Email">The <c>preferred_username</c>/<c>email</c> claim. Display + matching only, never the link key.</param>
/// <param name="DisplayName">The <c>name</c> claim.</param>
public sealed record EntraTokenIdentity(string TenantId, string ObjectId, string? Email, string? DisplayName);

/// <summary>Resolved app-registration credentials for one challenge.</summary>
/// <param name="OrganizationId">The org the sign-in is routed to.</param>
/// <param name="ClientId">The client id to challenge with (org's own, else deployment-wide).</param>
/// <param name="ConfigSource"><c>"org"</c> or <c>"system"</c> — which row holds the secret.</param>
public sealed record EntraChallengeConfig(int OrganizationId, string ClientId, string ConfigSource);

public enum EntraCompletionOutcome
{
    /// <summary>Signed in; <see cref="EntraCompletionResult.User"/> is set.</summary>
    Success,
    /// <summary>New account created, waiting for an org admin's approval.</summary>
    PendingApproval,
    /// <summary>The token's tenant isn't on any org's allow-list (or the org opted out).</summary>
    TenantNotAllowed,
    /// <summary>More than one org allows the tenant and the email domain doesn't decide it.</summary>
    Ambiguous,
    /// <summary>The token carried no usable email and no existing link matched.</summary>
    EmailMissing,
    /// <summary>A local account with this email exists in a different organisation.</summary>
    EmailTakenElsewhere,
    /// <summary>Matched an account that is still pending approval.</summary>
    AccountPending,
    /// <summary>Matched an account that has been disabled.</summary>
    AccountDisabled,
}

public sealed record EntraCompletionResult(EntraCompletionOutcome Outcome, User? User = null);

/// <summary>
/// Microsoft (Entra ID) sign-in: challenge routing, post-callback account
/// resolution, and link/JIT provisioning. See issue #552 for the design.
///
/// <para><b>Tenant-isolation note.</b> This service runs pre-auth (the sign-in
/// callback fires before any cookie exists), so its reads use
/// <c>IgnoreQueryFilters()</c> deliberately — the same sanctioned category as
/// login/signup/password-reset in <c>.design/auth-and-audit.md</c>. Every
/// cross-org read here is in service of routing one sign-in to exactly one
/// organisation; nothing is returned to a caller from another org's data.</para>
///
/// <para><b>The tid check is the security boundary.</b> With a multi-tenant
/// app registration, any Microsoft work account in the world produces a
/// cryptographically valid token. <see cref="CompleteAsync"/> therefore only
/// proceeds when the token's tenant id appears on the resolved organisation's
/// <c>entra_allowed_tenant_ids</c> — that list, not the signature, is what
/// keeps strangers out.</para>
/// </summary>
public sealed class EntraSignInService
{
    /// <summary>Provider discriminator stamped on <see cref="UserExternalLogin"/> rows.</summary>
    public const string ProviderName = "entra";

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly IDataProtector _orgSecretProtector;
    private readonly IDataProtector _systemSecretProtector;
    private readonly TimeProvider _clock;
    private readonly ILogger<EntraSignInService> _logger;

    public EntraSignInService(
        AppDbContext db,
        AuthService auth,
        IDataProtectionProvider protectionProvider,
        TimeProvider clock,
        ILogger<EntraSignInService> logger)
    {
        _db = db;
        _auth = auth;
        _orgSecretProtector = protectionProvider.CreateProtector(OrganizationAdminService.EntraClientSecretProtectionPurpose);
        _systemSecretProtector = protectionProvider.CreateProtector(SystemSettingsService.EntraClientSecretProtectionPurpose);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Whether the login page should offer "Sign in with Microsoft" at all:
    /// true when at least one organisation has it enabled.
    /// </summary>
    public Task<bool> IsSignInAvailableAsync(CancellationToken ct = default) =>
        _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(s => s.EntraEnabled, ct);

    /// <summary>
    /// Picks the organisation (and app registration) to challenge with.
    /// With an email we route via the claimed-domain table; without one we
    /// can only proceed when exactly one org has Entra enabled (the
    /// single-tenant shape). Returns a login-page error code otherwise:
    /// <c>entra-email-needed</c>, <c>entra-not-configured</c>.
    /// </summary>
    public async Task<(EntraChallengeConfig? Config, string? ErrorCode)> ResolveChallengeAsync(
        string? email, CancellationToken ct = default)
    {
        int? orgId = null;
        var domain = ExtractDomain(email);
        if (domain is not null)
        {
            orgId = await _db.OrganizationEmailDomains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Domain == domain)
                .Select(d => (int?)d.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }

        if (orgId is null)
        {
            var enabledOrgIds = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EntraEnabled)
                .Select(s => s.OrganizationId)
                .Take(2)
                .ToListAsync(ct);
            if (enabledOrgIds.Count == 1) orgId = enabledOrgIds[0];
            else if (enabledOrgIds.Count > 1) return (null, "entra-email-needed");
            else return (null, "entra-not-configured");
        }

        var settings = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == orgId && s.EntraEnabled)
            .Select(s => new { s.OrganizationId, s.EntraClientId })
            .FirstOrDefaultAsync(ct);
        if (settings is null) return (null, "entra-not-configured");

        if (settings.EntraClientId is not null)
        {
            return (new EntraChallengeConfig(settings.OrganizationId, settings.EntraClientId, "org"), null);
        }
        var systemClientId = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.EntraClientId)
            .FirstOrDefaultAsync(ct);
        return systemClientId is null
            ? (null, "entra-not-configured")
            : (new EntraChallengeConfig(settings.OrganizationId, systemClientId, "system"), null);
    }

    /// <summary>
    /// Decrypts the client secret for the code exchange. Called from the
    /// OIDC handler's token event so the secret never travels through the
    /// correlation cookie. Null when none is stored (PKCE-only registration).
    /// </summary>
    public async Task<string?> GetClientSecretAsync(int organizationId, string configSource, CancellationToken ct = default)
    {
        string? ciphertext;
        IDataProtector protector;
        if (configSource == "org")
        {
            ciphertext = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId)
                .Select(s => s.EntraClientSecretEncrypted)
                .FirstOrDefaultAsync(ct);
            protector = _orgSecretProtector;
        }
        else
        {
            ciphertext = await _db.SystemSettings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.EntraClientSecretEncrypted)
                .FirstOrDefaultAsync(ct);
            protector = _systemSecretProtector;
        }
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // Loud: a lost key ring silently breaks sign-in otherwise.
            _logger.LogError(ex, "Failed to decrypt Entra client secret (source={Source}, org={OrgId}).", configSource, organizationId);
            return null;
        }
    }

    /// <summary>
    /// Post-callback resolution: existing link, then email match, then JIT
    /// provisioning through the same approval machinery as verified signup.
    /// Every attempt (success or refusal) lands in <c>login_attempts</c> so
    /// the audit trail covers federated logins too.
    /// </summary>
    public async Task<EntraCompletionResult> CompleteAsync(EntraTokenIdentity token, string ip, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tid = token.TenantId.Trim().ToLowerInvariant();
        var email = AuthService.NormaliseEmail(token.Email ?? string.Empty);

        // 1. Existing link — the fast path for every returning user.
        var link = await _db.UserExternalLogins.IgnoreQueryFilters()
            .Include(l => l.User!).ThenInclude(u => u.Organization)
            .FirstOrDefaultAsync(l => l.Provider == ProviderName && l.Issuer == tid && l.Subject == token.ObjectId, ct);
        if (link is not null)
        {
            var linkedOrgAllows = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(s => s.OrganizationId == link.User!.OrganizationId
                    && s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid), ct);
            if (!linkedOrgAllows)
            {
                return await RefuseAsync(EntraCompletionOutcome.TenantNotAllowed, email, ip, now,
                    $"linked org no longer allows tenant {tid}", ct);
            }
            return await FinishStatusCheckedAsync(link.User!, link, email, ip, now, ct);
        }

        // 2. No link yet: which orgs would accept this tenant at all?
        var candidates = await _db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EntraEnabled && s.EntraAllowedTenantIds.Contains(tid))
            .Select(s => new { s.OrganizationId, s.AutoJoinVerifiedDomainUsers })
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return await RefuseAsync(EntraCompletionOutcome.TenantNotAllowed, email, ip, now,
                $"tenant {tid} on no org's allow-list", ct);
        }

        if (email.Length == 0)
        {
            return await RefuseAsync(EntraCompletionOutcome.EmailMissing, email, ip, now,
                $"token from tenant {tid} carried no email", ct);
        }

        // Email-domain routing decides when several orgs share the tenant.
        var domain = ExtractDomain(email);
        var domainOrgId = domain is null ? null : await _db.OrganizationEmailDomains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => (int?)d.OrganizationId)
            .FirstOrDefaultAsync(ct);
        var resolved = candidates.FirstOrDefault(c => c.OrganizationId == domainOrgId)
            ?? (candidates.Count == 1 ? candidates[0] : null);
        if (resolved is null)
        {
            return await RefuseAsync(EntraCompletionOutcome.Ambiguous, email, ip, now,
                $"tenant {tid} allowed by {candidates.Count} orgs and the email domain doesn't decide", ct);
        }

        // 3. Match an existing local account by verified email, in-org only.
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null && user.OrganizationId != resolved.OrganizationId)
        {
            return await RefuseAsync(EntraCompletionOutcome.EmailTakenElsewhere, email, ip, now,
                $"email registered in org {user.OrganizationId}, sign-in routed to org {resolved.OrganizationId}", ct);
        }
        if (user is not null)
        {
            var newLink = AddLink(user.Id, tid, token.ObjectId, email, now);
            _logger.LogInformation("Linked Entra identity {Tid}/{Oid} to existing user {Email} on first sign-in.", tid, token.ObjectId, email);
            return await FinishStatusCheckedAsync(user, newLink, email, ip, now, ct);
        }

        // 4. JIT provisioning, through the same approval machinery as the
        // verified signup flow (auto-join only when the email domain is
        // claimed by the resolved org and it opted in).
        var autoActive = resolved.OrganizationId == domainOrgId && resolved.AutoJoinVerifiedDomainUsers;
        user = new User
        {
            OrganizationId = resolved.OrganizationId,
            Email = email,
            // Entra-only account: no usable password. A random BCrypt hash
            // satisfies the NOT NULL column and can never verify.
            PasswordHash = _auth.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            DisplayName = string.IsNullOrWhiteSpace(token.DisplayName) ? email : token.DisplayName.Trim(),
            Role = UserRole.User,
            Status = autoActive ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
            LastLoginAt = autoActive ? now : null,
        };
        // Load the nav so a Success return can feed BuildIdentity, which
        // stamps org-name / MCP / tool claims from the Organization row.
        user.Organization = await _db.Organizations.IgnoreQueryFilters()
            .FirstAsync(o => o.Id == resolved.OrganizationId, ct);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        _db.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = resolved.OrganizationId,
            UserId = user.Id,
            Email = email,
            RequestedAt = now,
            Decision = autoActive ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = autoActive ? now : null,
            DecidedByUserId = autoActive ? user.Id : null,
        });
        AddLink(user.Id, tid, token.ObjectId, email, now, lastLogin: autoActive ? now : null);
        RecordAttempt(email, ip, autoActive, now);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("JIT-provisioned {Email} into org {OrgId} via Entra tenant {Tid} (active={Active}).",
            email, resolved.OrganizationId, tid, autoActive);
        return autoActive
            ? new EntraCompletionResult(EntraCompletionOutcome.Success, user)
            : new EntraCompletionResult(EntraCompletionOutcome.PendingApproval, user);
    }

    private UserExternalLogin AddLink(int userId, string tid, string objectId, string email, DateTime now, DateTime? lastLogin = null)
    {
        var link = new UserExternalLogin
        {
            UserId = userId,
            Provider = ProviderName,
            Issuer = tid,
            Subject = objectId,
            DisplayIdentity = email,
            CreatedAt = now,
            LastLoginAt = lastLogin,
        };
        _db.UserExternalLogins.Add(link);
        return link;
    }

    private async Task<EntraCompletionResult> FinishStatusCheckedAsync(
        User user, UserExternalLogin link, string email, string ip, DateTime now, CancellationToken ct)
    {
        if (user.Status == UserStatus.Pending)
        {
            RecordAttempt(email, ip, succeeded: false, now);
            await _db.SaveChangesAsync(ct);
            return new EntraCompletionResult(EntraCompletionOutcome.AccountPending, user);
        }
        if (user.Status == UserStatus.Disabled)
        {
            RecordAttempt(email, ip, succeeded: false, now);
            await _db.SaveChangesAsync(ct);
            return new EntraCompletionResult(EntraCompletionOutcome.AccountDisabled, user);
        }
        user.LastLoginAt = now;
        link.LastLoginAt = now;
        RecordAttempt(email, ip, succeeded: true, now);
        await _db.SaveChangesAsync(ct);
        return new EntraCompletionResult(EntraCompletionOutcome.Success, user);
    }

    private async Task<EntraCompletionResult> RefuseAsync(
        EntraCompletionOutcome outcome, string email, string ip, DateTime now,
        string reason, CancellationToken ct)
    {
        RecordAttempt(email, ip, succeeded: false, now);
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Entra sign-in refused ({Outcome}): {Reason}", outcome, reason);
        return new EntraCompletionResult(outcome);
    }

    private void RecordAttempt(string email, string ip, bool succeeded, DateTime now) =>
        _db.LoginAttempts.Add(new LoginAttempt
        {
            Email = email,
            Ip = ip,
            Succeeded = succeeded,
            Timestamp = now,
        });

    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        return email[(at + 1)..].Trim().ToLowerInvariant();
    }
}
