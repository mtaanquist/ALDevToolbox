using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.SingleTenant;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Result of a /login attempt. Discriminates the failure modes the UI cares
/// about so the page can render a specific (but never enumerating) message.
/// </summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    Pending,
    Disabled,
    LockedOut,
    RateLimited,
    /// <summary>
    /// Password verified but the user has 2FA enrolled — caller must redirect
    /// to <c>/login/challenge</c>. <c>LastLoginAt</c> is <em>not</em> stamped
    /// at this stage; <c>AuthService.CompleteMfaAsync</c> finalises the login
    /// once the second factor checks out.
    /// </summary>
    MfaRequired,
}

/// <summary>
/// Outcome of a /signup attempt.
/// </summary>
public enum SignupOutcome
{
    PendingApproval,
    EmailAlreadyTaken,
    OrganizationProvisioned,
    /// <summary>
    /// A verified email at an org's claimed domain joined that org as an Active
    /// member because the org has <c>AutoJoinVerifiedDomainUsers</c> on. The
    /// caller signs the user in (like <see cref="OrganizationProvisioned"/>)
    /// rather than queuing them for approval.
    /// </summary>
    JoinedActive,
    Invalid,
}

/// <summary>
/// User-initiated lifecycle on the account: signup (pending or auto-provision)
/// and the signed-in self-service surface (change password, change display
/// name, delete account). Slimmed in #88: login + lockout live in
/// <see cref="AuthService"/>, admin user management in
/// <see cref="UserAdministrationService"/>, credential-recovery tokens in
/// <see cref="PasswordResetService"/>.
/// </summary>
/// <remarks>
/// Depends on <see cref="AuthService"/> for password hashing /
/// verification so the BCrypt work factor and the password policy live in
/// exactly one place.
/// </remarks>
public sealed class AccountService
{
    private static readonly Regex SlugRegex = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly SystemSettingsService _settings;
    private readonly ISingleTenantMode _singleTenant;
    private readonly ILogger<AccountService> _logger;
    private readonly TimeProvider _clock;

    public AccountService(AppDbContext db, AuthService auth, SystemSettingsService settings, ISingleTenantMode singleTenant, ILogger<AccountService> logger, TimeProvider clock)
    {
        _db = db;
        _auth = auth;
        _settings = settings;
        _singleTenant = singleTenant;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Refuses self-service organisation creation when the deployment runs in
    /// single-tenant mode. Joining a claimed domain or an existing org by slug
    /// stays open — onboarding goes through admin invites or domain routing.
    /// </summary>
    private void ThrowIfNewOrgBlocked()
    {
        if (!_singleTenant.IsEnabled) return;
        throw new PlanValidationException(new Dictionary<string, string>
        {
            ["Email"] = "This site doesn't allow creating new organisations. Ask an administrator to invite you.",
        });
    }

    /// <summary>
    /// Creates a pending user (and signup request) for an existing or
    /// brand-new organisation. Resolution order:
    /// <list type="number">
    ///   <item>If the email matches a domain claimed by an org via
    ///         <see cref="OrganizationAdminService.AddEmailDomainAsync"/>,
    ///         the user is routed to that org as Pending — typed slug and
    ///         org name are ignored (the admin-claimed domain is
    ///         authoritative, so a domain squatter can't spin up a fake
    ///         org).</item>
    ///   <item>Otherwise, if <paramref name="organizationSlug"/> matches an
    ///         existing org, the user is attached as Pending.</item>
    ///   <item>Otherwise a brand-new org is created with the supplied
    ///         <paramref name="organizationName"/>; the slug is derived from
    ///         that name (not the user's display name). The new admin is
    ///         auto-approved because no in-org admin exists to do the
    ///         approving (see <c>.design/auth-and-audit.md</c>).</item>
    /// </list>
    /// </summary>
    public async Task<(SignupOutcome Outcome, User? User, Organization? Organization)> SignupAsync(
        string email, string displayName, string password, string? organizationSlug,
        string? organizationName, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        ValidateEmail(email, errors);
        ValidateDisplayName(displayName, errors);
        AuthService.ValidatePassword(password, errors);
        var slug = (organizationSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length > 0 && !SlugRegex.IsMatch(slug))
        {
            errors["OrganizationSlug"] = "Slug must use lowercase letters, digits and hyphens only.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var normalised = AuthService.NormaliseEmail(email);

        // Site-wide email-domain allow-list. When the SiteAdmin has filled it
        // in, every signup route (new-org, slug-join, claimed-domain) must
        // come from a listed domain. Empty/null list = feature off. Run after
        // shape validation so users see "valid email" before "wrong domain".
        var allowed = await _settings.GetSignupAllowedDomainsAsync(ct);
        if (allowed is not null)
        {
            var domain = EmailAddress.DomainOf(normalised) ?? string.Empty;
            if (!allowed.Contains(domain))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Email"] = "Sign-ups from this email domain are not allowed on this site. Contact the site administrator if you believe this is a mistake.",
                });
            }
        }

        var existingUser = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);
        if (existingUser is not null)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        Organization org;
        bool createdNewOrg = false;

        var domainMatch = await ResolveOrganizationByEmailDomainAsync(normalised, ct);
        if (domainMatch is not null)
        {
            org = domainMatch;
        }
        else if (slug.Length == 0)
        {
            ThrowIfNewOrgBlocked();
            ValidateOrganizationName(organizationName, errors);
            if (errors.Count > 0) throw new PlanValidationException(errors);
            var trimmedName = organizationName!.Trim();
            org = await CreatePendingOrganizationAsync(Slugify(trimmedName), trimmedName, now, ct);
            createdNewOrg = true;
        }
        else
        {
            var match = await _db.Organizations
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Slug == slug, ct);
            if (match is null)
            {
                ThrowIfNewOrgBlocked();
                ValidateOrganizationName(organizationName, errors);
                if (errors.Count > 0) throw new PlanValidationException(errors);
                var trimmedName = organizationName!.Trim();
                org = await CreatePendingOrganizationAsync(slug, trimmedName, now, ct);
                createdNewOrg = true;
            }
            else
            {
                org = match;
            }
        }

        var user = new User
        {
            OrganizationId = org.Id,
            Email = normalised,
            PasswordHash = _auth.HashPassword(password),
            DisplayName = displayName.Trim(),
            // New orgs auto-approve their first signup: there's no admin
            // in-org to do the approving and we deliberately have no superuser
            // (see .design/auth-and-audit.md). Existing-org signups still
            // wait on a same-org admin via /admin/users.
            Role = createdNewOrg ? UserRole.Admin : UserRole.User,
            Status = createdNewOrg ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        var request = new SignupRequest
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Email = normalised,
            RequestedAt = now,
            // Auto-approval still writes a SignupRequest so /admin/users keeps
            // a complete history; the decided fields point back at the user
            // themselves so the audit log is unambiguous.
            Decision = createdNewOrg ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = createdNewOrg ? now : null,
            DecidedByUserId = createdNewOrg ? user.Id : null,
        };
        _db.SignupRequests.Add(request);
        if (createdNewOrg)
        {
            org.IsPending = false;
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Signup recorded for {Email} into {OrgSlug} (new={New}).",
            normalised, org.Slug, createdNewOrg);

        return (createdNewOrg ? SignupOutcome.OrganizationProvisioned : SignupOutcome.PendingApproval,
                user, org);
    }

    /// <summary>
    /// Completes the email-first signup flow for a verified
    /// <see cref="PendingSignup"/>. Branches on whether the verified email's
    /// domain is claimed by an org:
    /// <list type="bullet">
    ///   <item>Claimed domain → join that org. Active <see cref="UserRole.User"/>
    ///         when the org has <see cref="OrganizationSettings.AutoJoinVerifiedDomainUsers"/>
    ///         on, otherwise Pending awaiting admin approval.</item>
    ///   <item>No claimed domain → create a brand-new org and activate the
    ///         visitor as its <see cref="UserRole.Admin"/>, exactly like the
    ///         new-org path of <see cref="SignupAsync"/>. There is no
    ///         "join an arbitrary org by slug" path here by design.</item>
    /// </list>
    /// The supplied <paramref name="pending"/> row must already be verified; the
    /// caller (the completion endpoint) re-checks that via
    /// <c>PendingSignupService.FindVerifiedAsync</c>. We stamp its
    /// <see cref="PendingSignup.CompletedAt"/> in the same save as the new user
    /// so the link/code can't be reused.
    /// </summary>
    public async Task<(SignupOutcome Outcome, User? User, Organization? Organization)> CompleteVerifiedSignupAsync(
        PendingSignup pending, string displayName, string password,
        string? organizationName, string? organizationSlug, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        ValidateDisplayName(displayName, errors);
        AuthService.ValidatePassword(password, errors);
        var slug = (organizationSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (slug.Length > 0 && !SlugRegex.IsMatch(slug))
        {
            errors["OrganizationSlug"] = "Slug must use lowercase letters, digits and hyphens only.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var normalised = pending.Email;
        var now = _clock.GetUtcNow().UtcDateTime;

        // Race: a user for this email could have appeared between verification
        // and completion (e.g. an admin invite was accepted in the meantime).
        // Refuse rather than duplicate — mirrors the SignupAsync guard.
        var existingUser = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == normalised, ct);
        if (existingUser)
        {
            return (SignupOutcome.EmailAlreadyTaken, null, null);
        }

        Organization org;
        var createdNewOrg = false;
        bool autoActive;

        var domainMatch = await ResolveOrganizationByEmailDomainAsync(normalised, ct);
        if (domainMatch is not null)
        {
            org = domainMatch;
            // Verified ownership of a claimed domain joins active immediately
            // only when the org opted in; otherwise it's the historical
            // Pending-awaiting-approval behaviour.
            autoActive = await _db.OrganizationSettings.IgnoreQueryFilters()
                .Where(s => s.OrganizationId == org.Id)
                .Select(s => (bool?)s.AutoJoinVerifiedDomainUsers)
                .FirstOrDefaultAsync(ct) ?? false;
        }
        else
        {
            // No claimed domain → the only path is a brand-new org.
            ThrowIfNewOrgBlocked();
            ValidateOrganizationName(organizationName, errors);
            if (errors.Count > 0) throw new PlanValidationException(errors);
            var trimmedName = organizationName!.Trim();
            org = await CreatePendingOrganizationAsync(slug.Length > 0 ? slug : trimmedName, trimmedName, now, ct);
            createdNewOrg = true;
            autoActive = true;
        }

        var user = new User
        {
            OrganizationId = org.Id,
            Email = normalised,
            PasswordHash = _auth.HashPassword(password),
            DisplayName = displayName.Trim(),
            Role = createdNewOrg ? UserRole.Admin : UserRole.User,
            Status = autoActive ? UserStatus.Active : UserStatus.Pending,
            CreatedAt = now,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        var request = new SignupRequest
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Email = normalised,
            RequestedAt = now,
            Decision = autoActive ? SignupDecision.Approved : SignupDecision.Pending,
            DecidedAt = autoActive ? now : null,
            DecidedByUserId = autoActive ? user.Id : null,
        };
        _db.SignupRequests.Add(request);
        if (createdNewOrg) org.IsPending = false;

        // Single-use: burn the verification row alongside the new account.
        pending.CompletedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Verified signup completed for {Email} into {OrgSlug} (new={New}, active={Active}).",
            normalised, org.Slug, createdNewOrg, autoActive);

        var outcome = autoActive
            ? (createdNewOrg ? SignupOutcome.OrganizationProvisioned : SignupOutcome.JoinedActive)
            : SignupOutcome.PendingApproval;
        return (outcome, user, org);
    }

    /// <summary>Self-service password change. Verifies the current password before applying the new one.</summary>
    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        if (!_auth.VerifyPassword(currentPassword, user.PasswordHash))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["CurrentPassword"] = "Current password is incorrect." });
        }
        var errors = new Dictionary<string, string>();
        AuthService.ValidatePassword(newPassword, errors, fieldName: "NewPassword");
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.PasswordHash = _auth.HashPassword(newPassword);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Self-service display-name change.</summary>
    public async Task ChangeDisplayNameAsync(int userId, string newDisplayName, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var errors = new Dictionary<string, string>();
        ValidateDisplayName(newDisplayName, errors);
        if (errors.Count > 0) throw new PlanValidationException(errors);
        user.DisplayName = newDisplayName.Trim();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes the user from their organisation. If they're the last active
    /// admin, requires <paramref name="acceptOrgDeletion"/> to be true; the
    /// org is then cascaded away with all its content. Otherwise the user
    /// row is deleted and the org keeps running.
    /// </summary>
    public async Task DeleteAccountAsync(int userId, bool acceptOrgDeletion, CancellationToken ct = default)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == userId, ct);

        var isLastActiveAdmin = user.Role == UserRole.Admin
            && user.Status == UserStatus.Active
            && await CountActiveAdminsAsync(user.OrganizationId, ct) <= 1;

        // Refined guard: if the user is the last active admin AND there are
        // other members in the org, refuse outright — they have to promote
        // somebody first. The "only member in the org" case still cascades
        // the org (handled in the branch below) because there's nothing left
        // to keep alive.
        var orgDeleted = false;
        if (isLastActiveAdmin)
        {
            var otherMembersExist = await _db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.OrganizationId == user.OrganizationId && u.Id != user.Id, ct);
            if (otherMembersExist)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["LastAdmin"] = "You're the last active admin. Promote another user to admin before deleting your account."
                });
            }
            if (!acceptOrgDeletion)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["LastAdmin"] = "You're the last user in this organisation. Deleting your account also deletes the organisation."
                });
            }
            var org = await _db.Organizations.IgnoreQueryFilters().FirstAsync(o => o.Id == user.OrganizationId, ct);
            await _db.AnonymiseOrganizationAsync(org.Id, ct);
            _db.Organizations.Remove(org);
            orgDeleted = true;
        }
        else
        {
            await _db.AnonymiseActorAsync(user.Id, ct);
            _db.Users.Remove(user);
        }
        await _db.SaveChangesAsync(ct);

        if (orgDeleted)
        {
            // The org cascade removed its oe_module_files rows, but the shared
            // oe_file_contents blobs (FK Restrict, possibly used by other orgs)
            // are left behind. Reclaim any that are now orphaned. Raw SQL so the
            // NOT EXISTS spans all orgs — blobs still referenced elsewhere stay.
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM oe_file_contents c WHERE NOT EXISTS " +
                "(SELECT 1 FROM oe_module_files f WHERE f.content_hash = c.content_hash)",
                ct);
        }
    }

    private static void ValidateEmail(string? value, Dictionary<string, string> errors)
    {
        if (!EmailAddress.HasValidShape(value))
        {
            errors["Email"] = "Enter a valid email address.";
        }
    }

    private static void ValidateDisplayName(string? value, Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            errors["DisplayName"] = "Display name must be 2–80 characters.";
        }
    }

    private static void ValidateOrganizationName(string? value, Dictionary<string, string> errors)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 2 or > 80)
        {
            errors["OrganizationName"] = "Organisation name must be 2–80 characters.";
        }
    }

    private async Task<Organization?> ResolveOrganizationByEmailDomainAsync(string normalisedEmail, CancellationToken ct)
    {
        var domain = EmailAddress.DomainOf(normalisedEmail);
        if (domain is null) return null;
        return await _db.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.Domain == domain)
            .Select(d => d.Organization!)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int> CountActiveAdminsAsync(int orgId, CancellationToken ct)
    {
        return await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == orgId
                             && u.Role == UserRole.Admin
                             && u.Status == UserStatus.Active, ct);
    }

    private async Task<Organization> CreatePendingOrganizationAsync(string slug, string organizationName, DateTime now, CancellationToken ct)
    {
        var safeSlug = Slugify(slug);
        var candidate = safeSlug;
        var disambiguator = 1;
        while (await _db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == candidate, ct))
        {
            disambiguator++;
            candidate = $"{safeSlug}-{disambiguator}";
        }
        var org = new Organization
        {
            Name = string.IsNullOrWhiteSpace(organizationName) ? candidate : organizationName,
            Slug = candidate,
            IsPending = true,
            IsSystem = false,
            CreatedAt = now,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(ct);

        // Seed the platform-default workspace files (.gitignore, the shared
        // ruleset, the README stub) so the new org has a starter library in
        // place when its admins create their first template. Idempotent: the
        // helper skips any row whose path already exists for the org.
        await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(_db, org.Id, now, ct);
        await _db.SaveChangesAsync(ct);
        return org;
    }

    private static string Slugify(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var chars = lower.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var hyphenated = new string(chars).Trim('-');
        while (hyphenated.Contains("--")) hyphenated = hyphenated.Replace("--", "-");
        return hyphenated.Length == 0 ? "org" : hyphenated;
    }
}
