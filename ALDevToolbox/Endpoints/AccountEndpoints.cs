using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
            AuthenticationService auth,
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
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var (outcome, user, org) = await accounts.SignupAsync(
                    email: form["Email"].ToString(),
                    displayName: form["DisplayName"].ToString(),
                    password: form["Password"].ToString(),
                    organizationSlug: form["OrganizationSlug"].ToString(),
                    ct);

                if (outcome == SignupOutcome.EmailAlreadyTaken)
                {
                    ctx.Response.Redirect("/signup?err=email-taken");
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

        return app;
    }
}
