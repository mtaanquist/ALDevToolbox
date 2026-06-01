using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // /auth/login: validates the email + password, sets the auth cookie
        // with the user_id / org_id / role / email claims, and triggers
        // seeding for orgs being touched by their first admin login.
        app.MapPost("/auth/login", async (
            HttpContext ctx,
            AuthService auth,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Auth");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var email = form["Email"].ToString();
            var password = form["Password"].ToString();
            var requestedReturn = form["ReturnUrl"].ToString();
            var safeReturn = ResolveSafeReturn(requestedReturn);
            var ip = ResolveIp(ctx);

            var (outcome, user) = await auth.TryLoginAsync(email, password, ip, ct);

            if (outcome == LoginOutcome.MfaRequired && user is not null)
            {
                // Password verified but a second factor is required. Stash the
                // user id + enrolled methods in a short-lived signed cookie and
                // redirect to /login/challenge.
                SetMfaPendingCookie(ctx, protection, new MfaPending(
                    user.Id, user.TotpEnabled, user.EmailMfaEnabled,
                    clock.GetUtcNow().UtcDateTime, safeReturn));
                ctx.Response.Redirect("/login/challenge");
                return;
            }

            if (outcome != LoginOutcome.Success || user is null)
            {
                var code = outcome switch
                {
                    LoginOutcome.Pending => "pending",
                    LoginOutcome.Disabled => "disabled",
                    LoginOutcome.LockedOut => "locked",
                    LoginOutcome.RateLimited => "rate-limited",
                    _ => "invalid",
                };
                logger.LogInformation("Login attempt for {Email} from {Ip} resolved {Outcome}.", email, ip, outcome);
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={code}&return={Uri.EscapeDataString(safeReturn)}");
                return;
            }

            var identity = BuildIdentity(user);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
            logger.LogInformation("Signed in {Email} (org {OrgId}, role {Role}).", user.Email, user.OrganizationId, user.Role);
            ctx.Response.Redirect(safeReturn);
        });

        app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/");
        });

        // /auth/signup — two paths:
        //  - Existing-org signup: creates a Pending user, emails the org's
        //    active admins, redirects with a "queued" message.
        //  - New-org signup: auto-approves (we have no superuser to do
        //    otherwise), signs the user in, and lands them on the home page
        //    as the new org's admin.
        // Email send failures log a warning but never roll back the signup.
        app.MapPost("/auth/signup", async (
            HttpContext ctx,
            AccountService accounts,
            AppDbContext db,
            IEmailService email,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            // When SMTP is configured, the email-first verified flow is the only
            // signup path. Refuse this unverified single-form POST so a forged
            // request can't skip verification — the /signup page renders the
            // email-only form in that case and posts to /auth/signup/start.
            if (await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/signup");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var (outcome, user, org) = await accounts.SignupAsync(
                    email: form["Email"].ToString(),
                    displayName: form["DisplayName"].ToString(),
                    password: form["Password"].ToString(),
                    organizationSlug: form["OrganizationSlug"].ToString(),
                    organizationName: form["OrganizationName"].ToString(),
                    ct);

                if (outcome == SignupOutcome.EmailAlreadyTaken)
                {
                    // Don't leak account existence. Return the same response
                    // shape as a queued-pending signup so an attacker can't
                    // tell a registered email apart from a fresh one. The
                    // info-level log retains the discriminator for forensics.
                    logger.LogInformation("Signup attempt with already-registered email — returning generic pending response.");
                    ctx.Response.Redirect("/signup?ok=pending");
                    return;
                }

                if (outcome == SignupOutcome.OrganizationProvisioned && user is not null && org is not null)
                {
                    user.Organization = org;
                    await ctx.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(BuildIdentity(user)));
                    logger.LogInformation("Auto-approved new-org signup {Email} as admin of {OrgSlug}.", user.Email, org.Slug);
                    ctx.Response.Redirect("/");
                    return;
                }

                // Existing-org signup: notify the org's active admins so they
                // can approve via /admin/users. SMTP failures don't roll back
                // the signup.
                if (org is not null && await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        var admins = await db.Users.IgnoreQueryFilters()
                            .Where(u => u.OrganizationId == org.Id
                                        && u.Role == UserRole.Admin
                                        && u.Status == UserStatus.Active)
                            .ToListAsync(ct);
                        foreach (var admin in admins)
                        {
                            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}{RouteConstants.AdminUsers}";
                            var (subject, body) = EmailTemplates.SignupPending(admin.DisplayName, user!.Email, org.Name, url);
                            await email.SendAsync(admin.Email, subject, body, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to email admins about pending signup {Email}.", user?.Email);
                    }
                }

                ctx.Response.Redirect("/signup?ok=pending");
            }
            catch (PlanValidationException ex)
            {
                var qs = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/signup?err=invalid&field={Uri.EscapeDataString(qs.Key)}&msg={Uri.EscapeDataString(qs.Value)}");
            }
        });

        // --- Email-first verified signup (when SMTP is configured) ----------
        //
        // Step 1: /auth/signup/start takes just an email and emails a one-time
        // verification link + 6-digit code. Step 2: the visitor verifies via
        // /auth/signup/verify (link) or /auth/signup/verify-code (code), which
        // sets the signed verified-email cookie. Step 3: /signup/details posts
        // the remaining fields to /auth/signup/complete. See
        // .design/auth-and-audit.md.

        app.MapPost("/auth/signup/start", async (
            HttpContext ctx,
            PendingSignupService pending,
            IEmailService email,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailInput = form["Email"].ToString();
            var ip = ResolveIp(ctx);
            try
            {
                var start = await pending.StartAsync(emailInput, ip, ct);
                if (start is not null && await email.IsConfiguredAsync(ct))
                {
                    var verifyUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/signup/verify?token={Uri.EscapeDataString(start.LinkToken)}";
                    var (subject, body) = EmailTemplates.SignupVerification(verifyUrl, start.Code);
                    await email.SendAsync(AuthService.NormaliseEmail(emailInput), subject, body, ct);
                }
            }
            catch (Exception ex)
            {
                // A send failure must not betray whether the address exists —
                // log it and fall through to the same generic response.
                logger.LogWarning(ex, "Failed to send signup verification email.");
            }
            // Always identical, regardless of new / already-registered / rate-
            // limited / domain-disallowed: no account enumeration.
            ctx.Response.Redirect($"/signup?ok=check-email&email={Uri.EscapeDataString(emailInput.Trim())}");
        });

        app.MapGet("/auth/signup/verify", async (
            HttpContext ctx,
            PendingSignupService pending,
            IDataProtectionProvider protection,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var token = ctx.Request.Query["token"].ToString();
            var row = await pending.VerifyByTokenAsync(token, ct);
            if (row is null)
            {
                ctx.Response.Redirect("/signup?err=verify-invalid");
                return;
            }
            SetSignupVerifiedCookie(ctx, protection, new SignupVerified(row.Id, row.Email, clock.GetUtcNow().UtcDateTime));
            ctx.Response.Redirect("/signup/details");
        });

        app.MapPost("/auth/signup/verify-code", async (
            HttpContext ctx,
            PendingSignupService pending,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailInput = form["Email"].ToString();
            var row = await pending.VerifyByCodeAsync(emailInput, form["Code"].ToString(), ct);
            if (row is null)
            {
                ctx.Response.Redirect($"/signup?err=code-invalid&email={Uri.EscapeDataString(emailInput.Trim())}");
                return;
            }
            SetSignupVerifiedCookie(ctx, protection, new SignupVerified(row.Id, row.Email, clock.GetUtcNow().UtcDateTime));
            ctx.Response.Redirect("/signup/details");
        });

        app.MapPost("/auth/signup/complete", async (
            HttpContext ctx,
            PendingSignupService pending,
            AccountService accounts,
            AppDbContext db,
            IEmailService email,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var state = ReadSignupVerifiedCookie(ctx, protection, clock);
            if (state is null)
            {
                ctx.Response.Redirect("/signup");
                return;
            }

            // Authoritative re-check: the cookie is only a hint. The row must
            // still be verified, uncompleted and unexpired.
            var row = await pending.FindVerifiedAsync(state.Email, ct);
            if (row is null)
            {
                ClearSignupVerifiedCookie(ctx);
                ctx.Response.Redirect("/signup?err=verify-invalid");
                return;
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var (outcome, user, org) = await accounts.CompleteVerifiedSignupAsync(
                    row,
                    displayName: form["DisplayName"].ToString(),
                    password: form["Password"].ToString(),
                    organizationName: form["OrganizationName"].ToString(),
                    organizationSlug: form["OrganizationSlug"].ToString(),
                    ct);

                ClearSignupVerifiedCookie(ctx);

                if (outcome == SignupOutcome.EmailAlreadyTaken)
                {
                    // Lost a race to an invite/another signup — the account now
                    // exists, so send them to sign in.
                    logger.LogInformation("Verified signup raced an existing account; redirecting to login.");
                    ctx.Response.Redirect(RouteConstants.Login);
                    return;
                }

                if ((outcome == SignupOutcome.OrganizationProvisioned || outcome == SignupOutcome.JoinedActive)
                    && user is not null && org is not null)
                {
                    user.Organization = org;
                    await ctx.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(BuildIdentity(user)));
                    logger.LogInformation("Verified signup signed in {Email} (org {OrgSlug}, newOrg={New}).",
                        user.Email, org.Slug, outcome == SignupOutcome.OrganizationProvisioned);
                    ctx.Response.Redirect("/");
                    return;
                }

                // PendingApproval — notify the org's active admins, then show
                // the queued message. SMTP failures don't roll back the signup.
                if (org is not null && await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        var admins = await db.Users.IgnoreQueryFilters()
                            .Where(u => u.OrganizationId == org.Id
                                        && u.Role == UserRole.Admin
                                        && u.Status == UserStatus.Active)
                            .ToListAsync(ct);
                        foreach (var admin in admins)
                        {
                            var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}{RouteConstants.AdminUsers}";
                            var (subject, body) = EmailTemplates.SignupPending(admin.DisplayName, user!.Email, org.Name, url);
                            await email.SendAsync(admin.Email, subject, body, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to email admins about pending signup {Email}.", user?.Email);
                    }
                }

                ctx.Response.Redirect("/signup?ok=pending");
            }
            catch (PlanValidationException ex)
            {
                var qs = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/signup/details?err=invalid&field={Uri.EscapeDataString(qs.Key)}&msg={Uri.EscapeDataString(qs.Value)}");
            }
        });

        app.MapPost("/auth/forgot-password", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            AppDbContext db,
            IEmailService email,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("ForgotPassword");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/forgot-password?err=not-configured");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var addr = form["Email"].ToString();
            try
            {
                var token = await passwordReset.CreatePasswordResetTokenAsync(addr, ct);
                if (token is not null)
                {
                    var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == addr.Trim().ToLowerInvariant(), ct);
                    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/reset-password?token={Uri.EscapeDataString(token)}";
                    var (subject, body) = EmailTemplates.ForgotPassword(user.DisplayName, url);
                    await email.SendAsync(user.Email, subject, body, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forgot-password flow failed for {Email}.", addr);
            }
            ctx.Response.Redirect("/forgot-password?ok=1");
        });

        // /auth/login/magic — issues a single-use magic-link sign-in token.
        // Always redirects to "ok=1" so the response is identical for known
        // and unknown emails. Per-email and per-IP rate limits applied in
        // PasswordResetService.CreateMagicLoginTokenAsync.
        app.MapPost("/auth/login/magic", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            AppDbContext db,
            IEmailService email,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MagicLogin");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/login/magic?err=not-configured");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var addr = form["Email"].ToString();
            try
            {
                var token = await passwordReset.CreateMagicLoginTokenAsync(addr, ResolveIp(ctx), ct);
                if (token is not null)
                {
                    var user = await db.Users.IgnoreQueryFilters()
                        .FirstAsync(u => u.Email == addr.Trim().ToLowerInvariant(), ct);
                    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/login/magic/consume?token={Uri.EscapeDataString(token)}";
                    var (subject, body) = EmailTemplates.MagicLink(user.DisplayName, url);
                    await email.SendAsync(user.Email, subject, body, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Magic-link flow failed for {Email}.", addr);
            }
            ctx.Response.Redirect("/login/magic?ok=1");
        });

        app.MapGet("/auth/login/magic/consume", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MagicLogin");
            var token = ctx.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(token))
            {
                ctx.Response.Redirect("/login/magic");
                return;
            }
            try
            {
                var user = await passwordReset.ConsumeMagicLoginTokenAsync(token, ct);
                await ctx.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(BuildIdentity(user)));
                logger.LogInformation("Magic-link sign-in for {Email} (org {OrgId}).", user.Email, user.OrganizationId);
                ctx.Response.Redirect("/");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/login/magic?err=invalid&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/accept-invite — invitee submits display name + password.
        // Activates the user into the inviting organisation and signs them in.
        app.MapPost("/auth/accept-invite", async (
            HttpContext ctx,
            InviteService invites,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AcceptInvite");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var token = form["Token"].ToString();
            try
            {
                var user = await invites.AcceptAsync(
                    token,
                    form["DisplayName"].ToString(),
                    form["Password"].ToString(),
                    ct);

                await ctx.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(BuildIdentity(user)));
                logger.LogInformation("Invite accepted; {Email} signed in to org {OrgId}.",
                    user.Email, user.OrganizationId);
                ctx.Response.Redirect("/");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect(
                    $"/accept-invite?token={Uri.EscapeDataString(token)}&err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/reset-password — consumes the token and applies the new password.
        app.MapPost("/auth/reset-password", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var token = form["Token"].ToString();
            var password = form["Password"].ToString();
            try
            {
                await passwordReset.ConsumePasswordResetTokenAsync(token, password, ct);
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.OkQuery}=password-reset");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect(
                    $"/reset-password?token={Uri.EscapeDataString(token)}&err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/account/* — self-service. All require [Authorize].
        app.MapPost("/auth/account/password", async (
            HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await accounts.ChangePasswordAsync(org.CurrentUserId!.Value,
                    form["CurrentPassword"].ToString(), form["NewPassword"].ToString(), ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=password");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/display-name", async (
            HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await accounts.ChangeDisplayNameAsync(org.CurrentUserId!.Value, form["DisplayName"].ToString(), ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=display-name");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/delete", async (
            HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var accept = form["AcceptOrgDeletion"] == "true" || form["AcceptOrgDeletion"] == "on";
            try
            {
                await accounts.DeleteAccountAsync(org.CurrentUserId!.Value, accept, ct);
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.OkQuery}=account-deleted");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        // --- Login MFA challenge -------------------------------------------

        async Task CompleteMfaSignIn(HttpContext ctx, AuthService auth, MfaPending state, CancellationToken ct, ILogger logger)
        {
            var user = await auth.CompleteMfaAsync(state.UserId, ResolveIp(ctx), ct);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(BuildIdentity(user)));
            ClearMfaPendingCookie(ctx);
            logger.LogInformation("MFA-gated sign-in completed for {Email} (org {OrgId}).", user.Email, user.OrganizationId);
            ctx.Response.Redirect(string.IsNullOrEmpty(state.ReturnUrl) ? "/" : state.ReturnUrl);
        }

        app.MapPost("/auth/login/challenge/totp", async (
            HttpContext ctx, AuthService auth, TotpService totp,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await totp.VerifyAsync(state.UserId, code, ct))
            {
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, ct, logger);
        });

        app.MapPost("/auth/login/challenge/email/issue", async (
            HttpContext ctx, EmailMfaService mfa, IEmailService emailSvc, AppDbContext db,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }

            try
            {
                if (!await emailSvc.IsConfiguredAsync(ct))
                {
                    ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=not-configured");
                    return;
                }
                var code = await mfa.IssueChallengeAsync(state.UserId, ct);
                if (code is null)
                {
                    ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=rate-limited");
                    return;
                }
                var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == state.UserId)
                    .Select(u => new { u.Email, u.DisplayName })
                    .FirstAsync(ct);
                var (subject, body) = EmailTemplates.MfaEmailCode(user.DisplayName, code);
                await emailSvc.SendAsync(user.Email, subject, body, ct);
                ctx.Response.Redirect($"/login/challenge?method=email&{RouteConstants.OkQuery}=sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email MFA challenge failed.");
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=invalid");
            }
        });

        app.MapPost("/auth/login/challenge/email/verify", async (
            HttpContext ctx, AuthService auth, EmailMfaService mfa,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await mfa.VerifyAsync(state.UserId, code, ct))
            {
                ctx.Response.Redirect($"/login/challenge?method=email&{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, ct, logger);
        });

        app.MapPost("/auth/login/challenge/recovery", async (
            HttpContext ctx, AuthService auth, RecoveryCodeService recovery,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await recovery.ConsumeAsync(state.UserId, code, ct))
            {
                ctx.Response.Redirect($"/login/challenge?method=recovery&{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, ct, logger);
        });

        // --- TOTP self-service ----------------------------------------------

        app.MapPost("/auth/account/2fa/totp/confirm", async (
            HttpContext ctx, TotpService totp, IOrganizationContext org,
            IDataProtectionProvider protection, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var codes = await totp.ConfirmEnrollmentAsync(org.CurrentUserId!.Value, form["Code"].ToString(), ct);
                // Stash the plaintext codes in a 60-second protected cookie so the
                // recovery-codes page can render them once.
                SetOneShotRecoveryCodesCookie(ctx, protection, codes);
                ctx.Response.Redirect("/account/2fa/recovery-codes?show=1");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/account/2fa/totp/setup?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/totp/disable", async (
            HttpContext ctx, TotpService totp, AuthService auth, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == org.CurrentUserId!.Value, ct);
            if (!auth.VerifyPassword(form["Password"].ToString(), user.PasswordHash))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Password&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Password is incorrect.")}");
                return;
            }
            await totp.DisableAsync(user.Id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=totp-disabled");
        }).RequireAuthorization();

        // --- Email-MFA self-service -----------------------------------------

        app.MapPost("/auth/account/2fa/email/begin", async (
            HttpContext ctx, EmailMfaService mfa, IEmailService emailSvc, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("EmailMfaSetup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await emailSvc.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("SMTP isn't configured. Ask a SiteAdmin.")}");
                return;
            }
            try
            {
                var code = await mfa.IssueChallengeAsync(org.CurrentUserId!.Value, ct);
                if (code is null)
                {
                    ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Try again in a few minutes.")}");
                    return;
                }
                var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == org.CurrentUserId!.Value)
                    .Select(u => new { u.Email, u.DisplayName })
                    .FirstAsync(ct);
                var (subject, body) = EmailTemplates.MfaEmailCode(user.DisplayName, code);
                await emailSvc.SendAsync(user.Email, subject, body, ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email-MFA setup failed.");
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Failed to send code.")}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/email/confirm", async (
            HttpContext ctx, EmailMfaService mfa, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            if (!await mfa.VerifyAsync(org.CurrentUserId!.Value, form["Code"].ToString(), ct))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Code&{RouteConstants.MsgQuery}={Uri.EscapeDataString("That code didn't match. Request a new one.")}");
                return;
            }
            await mfa.EnableAsync(org.CurrentUserId!.Value, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-enabled");
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/email/disable", async (
            HttpContext ctx, EmailMfaService mfa, AuthService auth, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == org.CurrentUserId!.Value, ct);
            if (!auth.VerifyPassword(form["Password"].ToString(), user.PasswordHash))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Password&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Password is incorrect.")}");
                return;
            }
            await mfa.DisableAsync(user.Id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-disabled");
        }).RequireAuthorization();

        // --- Recovery codes regen -------------------------------------------

        app.MapPost("/auth/account/2fa/recovery/regenerate", async (
            HttpContext ctx, RecoveryCodeService recovery, AuthService auth, AppDbContext db,
            IOrganizationContext org, IDataProtectionProvider protection,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == org.CurrentUserId!.Value, ct);
            if (!auth.VerifyPassword(form["Password"].ToString(), user.PasswordHash))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Password&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Password is incorrect.")}");
                return;
            }
            if (!user.TotpEnabled)
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Totp&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Enable TOTP first.")}");
                return;
            }
            var codes = await recovery.RegenerateAsync(user.Id, ct);
            SetOneShotRecoveryCodesCookie(ctx, protection, codes);
            ctx.Response.Redirect("/account/2fa/recovery-codes?show=1");
        }).RequireAuthorization();

        // --- Passkey self-service + login ------------------------------------

        app.MapGet("/account/passkeys/registration-options", async (
            HttpContext ctx, PasskeyService passkeys, IOrganizationContext org, CancellationToken ct) =>
        {
            if (org.CurrentUserId is null) { ctx.Response.StatusCode = 401; return; }
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("Passkeys not configured."); return; }
            var (options, envelope) = await passkeys.BeginRegistrationAsync(org.CurrentUserId.Value, ct);
            ctx.Response.Cookies.Append(PasskeyService.RegistrationCookieName, envelope, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                MaxAge = PasskeyService.ChallengeLifetime,
            });
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(options.ToJson(), ct);
        }).RequireAuthorization();

        app.MapPost("/account/passkeys", async (
            HttpContext ctx, PasskeyService passkeys, IOrganizationContext org, CancellationToken ct) =>
        {
            if (org.CurrentUserId is null) { ctx.Response.StatusCode = 401; return; }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var nickname = doc.RootElement.TryGetProperty("nickname", out var n) ? n.GetString() ?? "Passkey" : "Passkey";
            var rawResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
                doc.RootElement.GetProperty("response").GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (rawResponse is null) { ctx.Response.StatusCode = 400; return; }
            if (!ctx.Request.Cookies.TryGetValue(PasskeyService.RegistrationCookieName, out var envelope) || string.IsNullOrEmpty(envelope))
            {
                ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Registration session expired."); return;
            }
            ctx.Response.Cookies.Delete(PasskeyService.RegistrationCookieName);
            try
            {
                await passkeys.CompleteRegistrationAsync(org.CurrentUserId.Value, nickname, rawResponse, envelope, ct);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/passkeys/{id:int}/delete", async (
            int id, HttpContext ctx, PasskeyService passkeys, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await passkeys.DeleteAsync(org.CurrentUserId!.Value, id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=passkey-deleted");
        }).RequireAuthorization();

        app.MapPost("/auth/account/passkeys/{id:int}/rename", async (
            int id, HttpContext ctx, PasskeyService passkeys, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await passkeys.RenameAsync(org.CurrentUserId!.Value, id, form["Name"].ToString(), ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=passkey-renamed");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/passkey/login/options", async (
            HttpContext ctx, PasskeyService passkeys, CancellationToken ct) =>
        {
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("Passkeys not configured."); return; }
            string? emailHint = null;
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync(ct);
                emailHint = form["Email"].ToString();
            }
            else if (ctx.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("email", out var e)) emailHint = e.GetString();
                }
            }
            var (options, envelope) = await passkeys.BeginLoginAsync(emailHint, ct);
            ctx.Response.Cookies.Append(PasskeyService.LoginCookieName, envelope, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                MaxAge = PasskeyService.ChallengeLifetime,
            });
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(options.ToJson(), ct);
        });

        app.MapPost("/auth/passkey/login", async (
            HttpContext ctx, PasskeyService passkeys, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PasskeyLogin");
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; return; }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var rawResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (rawResponse is null) { ctx.Response.StatusCode = 400; return; }
            if (!ctx.Request.Cookies.TryGetValue(PasskeyService.LoginCookieName, out var envelope) || string.IsNullOrEmpty(envelope))
            {
                ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"session-expired\"}"); return;
            }
            ctx.Response.Cookies.Delete(PasskeyService.LoginCookieName);
            try
            {
                var user = await passkeys.CompleteLoginAsync(rawResponse, envelope, ct);
                await ctx.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(BuildIdentity(user)));
                logger.LogInformation("Passkey sign-in for {Email}.", user.Email);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Passkey login failed.");
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        // --- Personal access tokens (MCP / non-interactive callers) --------

        // Create: validates input, mints a token, stashes the plaintext in a
        // Data-Protected one-shot cookie, then redirects to the reveal screen.
        // The plaintext is shown exactly once — subsequent visits to the list
        // page show only the prefix.
        app.MapPost("/auth/account/access-tokens/create", async (
            HttpContext ctx,
            PersonalAccessTokenService tokens,
            IOrganizationContext org,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var userId = org.CurrentUserId;
            var orgId = org.CurrentOrganizationId;
            if (userId is null || orgId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var name = form["Name"].ToString();
            DateTime? expiresAt = null;
            var ttlDaysRaw = form["TtlDays"].ToString();
            if (int.TryParse(ttlDaysRaw, out var ttlDays) && ttlDays > 0)
            {
                expiresAt = DateTime.UtcNow.AddDays(ttlDays);
            }

            try
            {
                var issued = await tokens.IssueAsync(userId.Value, orgId.Value, name, expiresAt, ct);
                SetOneShotPatCookie(ctx, protection, new OneShotPat(
                    issued.Id, issued.Plaintext, name.Trim(), issued.CreatedAt, issued.ExpiresAt));
                ctx.Response.Redirect("/account/access-tokens/created");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                var msg = string.IsNullOrEmpty(first.Value) ? "Invalid token request." : first.Value;
                ctx.Response.Redirect($"/account/access-tokens?err={Uri.EscapeDataString(msg)}");
            }
        }).RequireAuthorization();

        // Revoke: stamps RevokedAt. No-op when the token doesn't belong to
        // the caller's org (the query filter hides it). Redirects back to
        // the list page either way.
        app.MapPost("/auth/account/access-tokens/{id:int}/revoke", async (
            int id,
            HttpContext ctx,
            PersonalAccessTokenService tokens,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await tokens.RevokeAsync(id, ignoreOrgScope: false, ct);
            ctx.Response.Redirect("/account/access-tokens?ok=revoked");
        }).RequireAuthorization();

        // --- Admin-initiated email change: user-side confirmation -----------

        app.MapGet("/auth/account/email-change/confirm", async (
            HttpContext ctx, UserAdministrationService users, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("EmailChange");
            var token = ctx.Request.Query["token"].ToString();
            var user = await users.ConfirmEmailChangeAsync(token, ct);
            if (user is null)
            {
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=email-change-invalid");
                return;
            }
            // Force the user to re-login from the new address so the cookie's
            // email claim is correct.
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            logger.LogInformation("Email change confirmed for user {UserId}; new email {Email}.", user.Id, user.Email);
            ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.OkQuery}=email-change-confirmed");
        });

        return app;
    }
}
