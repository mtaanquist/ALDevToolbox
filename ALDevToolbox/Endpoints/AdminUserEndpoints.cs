using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// /admin/users/* — approve / reject / disable / enable / invite. Admin-only.
/// Role changes run inside the Blazor circuit (interactive role dropdown on
/// AdminUsers.razor) and call UserAdministrationService.ChangeRoleAsync directly.
/// </summary>
internal static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/users/{id:int}/approve", async (
            int id, HttpContext ctx, UserAdministrationService users, AppDbContext db, IEmailService email,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminUsers");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await users.ApproveSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);
            if (await email.IsConfiguredAsync(ct))
            {
                try
                {
                    var req = await db.SignupRequests.IgnoreQueryFilters()
                        .Include(r => r.User).Include(r => r.Organization)
                        .FirstAsync(r => r.Id == id, ct);
                    if (req.User is not null && req.Organization is not null)
                    {
                        var loginUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{RouteConstants.Login}";
                        var (subject, body) = EmailTemplates.SignupDecided(
                            req.User.DisplayName, req.Organization.Name, approved: true, loginUrl);
                        await email.SendAsync(req.User.Email, subject, body, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Approval email failed for signup {Id}.", id);
                }
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/users/{id:int}/reject", async (
            int id, HttpContext ctx, UserAdministrationService users, AppDbContext db, IEmailService email,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminUsers");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var req = await db.SignupRequests.IgnoreQueryFilters()
                .Include(r => r.User).Include(r => r.Organization)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            var requesterEmail = req?.User?.Email;
            var requesterDisplay = req?.User?.DisplayName ?? "User";
            var orgName = req?.Organization?.Name ?? "Unknown organisation";

            await users.RejectSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);

            if (await email.IsConfiguredAsync(ct) && !string.IsNullOrEmpty(requesterEmail))
            {
                try
                {
                    var loginUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{RouteConstants.Login}";
                    var (subject, body) = EmailTemplates.SignupDecided(requesterDisplay, orgName, approved: false, loginUrl);
                    await email.SendAsync(requesterEmail, subject, body, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Rejection email failed for signup {Id}.", id);
                }
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/users/{id:int}/disable", async (
            int id, HttpContext ctx, UserAdministrationService users, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await users.DisableUserAsync(id, org.CurrentOrganizationId!.Value, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/users/{id:int}/enable", async (
            int id, HttpContext ctx, UserAdministrationService users, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await users.EnableUserAsync(id, org.CurrentOrganizationId!.Value, ct);
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/users/invite", async (
            HttpContext ctx,
            InviteService invites,
            AppDbContext db,
            IEmailService email,
            IOrganizationContext orgCtx,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminInvite");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}="
                    + Uri.EscapeDataString("Email isn't set up on this site. Ask a site administrator to configure it, or use the link-only flow above."));
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailAddr = form["Email"].ToString();
            var role = form["Role"].ToString() == "Admin" ? UserRole.Admin : UserRole.User;
            var message = form["WelcomeMessage"].ToString();
            try
            {
                var (token, inviteId) = await invites.CreateAsync(emailAddr, role, message, ct);
                var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/accept-invite?token={Uri.EscapeDataString(token)}";
                var inviter = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Include(u => u.Organization)
                    .FirstAsync(u => u.Id == orgCtx.CurrentUserId!.Value, ct);
                var orgName = inviter.Organization?.Name ?? "your organisation";
                var roleLabel = role == UserRole.Admin ? "Administrator" : "User";
                var (subject, body) = EmailTemplates.Invite(inviter.DisplayName, orgName, roleLabel, message, url);
                try
                {
                    await email.SendAsync(emailAddr.Trim(), subject, body, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Invite email failed for invite {InviteId} to {Email}.", inviteId, emailAddr);
                    ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}="
                        + Uri.EscapeDataString("Invite created but the email failed to send: " + ex.Message));
                    return;
                }
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.OkQuery}=invited");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        app.MapPost("/admin/users/invites/{id:int}/revoke", async (
            int id, HttpContext ctx, InviteService invites, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await invites.RevokeAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(ex.Errors.First().Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.OkQuery}=invite-revoked");
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // Admin-initiated user creation without requiring email. Issues an
        // invite the same way /admin/users/invite does, but surfaces the
        // accept URL in the admin UI (one-shot 60s protected cookie) instead
        // of relying on SMTP. If SMTP is configured AND the admin ticked
        // "SendEmail", also fires the existing invite email template.
        app.MapPost("/admin/users/create", async (
            HttpContext ctx,
            InviteService invites,
            AppDbContext db,
            IEmailService email,
            IOrganizationContext orgCtx,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminCreateUser");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailAddr = form["Email"].ToString();
            var role = form["Role"].ToString() == "Admin" ? UserRole.Admin : UserRole.User;
            var message = form["WelcomeMessage"].ToString();
            var sendEmail = form["SendEmail"] == "true" || form["SendEmail"] == "on";
            try
            {
                var (token, inviteId) = await invites.CreateAsync(emailAddr, role, message, ct);
                var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/accept-invite?token={Uri.EscapeDataString(token)}";
                if (sendEmail && await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        var inviter = await db.Users.IgnoreQueryFilters().AsNoTracking()
                            .Include(u => u.Organization)
                            .FirstAsync(u => u.Id == orgCtx.CurrentUserId!.Value, ct);
                        var orgName = inviter.Organization?.Name ?? "your organisation";
                        var roleLabel = role == UserRole.Admin ? "Administrator" : "User";
                        var (subject, body) = EmailTemplates.Invite(inviter.DisplayName, orgName, roleLabel, message, url);
                        await email.SendAsync(emailAddr.Trim(), subject, body, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Invite email failed for invite {InviteId}; the link is still surfaced inline.", inviteId);
                    }
                }
                SetOneShotInviteCookie(ctx, protection, url);
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.OkQuery}=created&inviteId={inviteId}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // Admin-initiated email change. Stamps users.pending_email, issues a
        // 24h EmailChangeConfirm token, sends a link to the NEW address (or
        // surfaces it inline via the one-shot cookie when SMTP is off).
        app.MapPost("/admin/users/{id:int}/email", async (
            int id, HttpContext ctx,
            UserAdministrationService userAdmin,
            AppDbContext db,
            IEmailService email,
            IOrganizationContext orgCtx,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminEmailChange");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var newEmail = form["NewEmail"].ToString();
            try
            {
                var token = await userAdmin.RequestEmailChangeAsync(id, newEmail,
                    orgCtx.CurrentOrganizationId!.Value, orgCtx.CurrentUserId!.Value, ct);
                var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/account/email-change/confirm?token={Uri.EscapeDataString(token)}";
                if (await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                            .FirstAsync(u => u.Id == id, ct);
                        var (subject, body) = EmailTemplates.EmailChangeConfirm(user.DisplayName, url);
                        await email.SendAsync(newEmail.Trim().ToLowerInvariant(), subject, body, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Email-change confirmation email failed for user {Id}; link surfaced inline.", id);
                        SetOneShotInviteCookie(ctx, protection, url);
                    }
                }
                else
                {
                    SetOneShotInviteCookie(ctx, protection, url);
                }
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.OkQuery}=email-pending&userId={id}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        return app;
    }
}
