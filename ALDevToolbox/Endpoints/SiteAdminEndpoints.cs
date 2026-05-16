using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// /site-admin/* — hosting-operator endpoints. The cookie events translate
/// auth failures on this prefix into 404 so non-SiteAdmins can't even confirm
/// the routes exist (see <c>Program.cs</c> cookie options).
/// </summary>
internal static class SiteAdminEndpoints
{
    public static IEndpointRouteBuilder MapSiteAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/site-admin/users/{id:int}/promote", async (
            int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await siteAdmin.PromoteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=promoted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/users/{id:int}/demote", async (
            int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await siteAdmin.DemoteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=demoted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        // Post to a distinct sub-path so we don't collide with the Razor
        // Components endpoint that MapRazorComponents registers for the
        // `/site-admin/settings` page route — overlapping the two raises
        // AmbiguousMatchException at request time.
        app.MapPost("/site-admin/settings/save", async (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var input = new SystemSettingsInput(
                SmtpHost: form["SmtpHost"].ToString(),
                SmtpPort: int.TryParse(form["SmtpPort"], out var port) ? port : null,
                SmtpUser: form["SmtpUser"].ToString(),
                SmtpPassword: form["SmtpPassword"].ToString(),
                ClearSmtpPassword: form["ClearSmtpPassword"] == "true" || form["ClearSmtpPassword"] == "on",
                SmtpFrom: form["SmtpFrom"].ToString(),
                SmtpUseStartTls: form.ContainsKey("SmtpUseStartTls")
                    ? (form["SmtpUseStartTls"] == "true" || form["SmtpUseStartTls"] == "on")
                    : null,
                BannerText: form["BannerText"].ToString(),
                DefaultSignupAutoApprove: form["DefaultSignupAutoApprove"] == "true" || form["DefaultSignupAutoApprove"] == "on",
                BackupScheduleEnabled: form["BackupScheduleEnabled"] == "true" || form["BackupScheduleEnabled"] == "on",
                BackupScheduleTimeUtc: TimeOnly.TryParse(form["BackupScheduleTimeUtc"], out var bst) ? bst : new TimeOnly(2, 0),
                BackupRetentionCount: int.TryParse(form["BackupRetentionCount"], out var brc) ? brc : 14,
                PerTenantBackupRetentionCount: int.TryParse(form["PerTenantBackupRetentionCount"], out var ptrc) ? ptrc : 30,
                DefaultStorageQuotaMb: string.IsNullOrWhiteSpace(form["DefaultStorageQuotaMb"])
                    ? null
                    : int.TryParse(form["DefaultStorageQuotaMb"], out var dsq) ? dsq : null,
                IndexSizeMultiplier: decimal.TryParse(form["IndexSizeMultiplier"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var ism)
                    ? ism
                    : 0.5m);

            try
            {
                await settings.SaveAsync(input, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.OkQuery}=saved");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(first.Value));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/test-email", async (
            HttpContext ctx, IEmailService email, AppDbContext db, IOrganizationContext orgCtx,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SiteAdminTestEmail");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString("SMTP is not configured."));
                return;
            }
            var userId = orgCtx.CurrentUserId;
            if (userId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var recipient = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstAsync(ct);
            try
            {
                var (subject, body) = EmailTemplates.SiteAdminTest(recipient.DisplayName);
                await email.SendAsync(recipient.Email, subject, body, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.OkQuery}=test-sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test email failed for SiteAdmin {Email}.", recipient.Email);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Test email failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/create", async (
            HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await backups.CreateAsync(BackupKind.AdHoc, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=created");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/pin", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, pinned: true, ct); }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=pinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/unpin", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, pinned: false, ct); }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=unpinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/delete", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.DeleteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=deleted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/restore", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("RestoreEndpoint");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
            if (!confirmed)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Tick the confirmation box before restoring."));
                return;
            }
            try
            {
                await backups.RestoreAsync(id, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=restored");
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Restore failed for backup {Id}.", id);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString("Restore failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        // SiteAdmin break-glass: wipe a user's 2FA when they've lost access
        // to every factor (no recovery code, no TOTP device, no passkey, SMTP
        // down). Logs at info; the audit interceptor captures the row changes
        // for the audit log.
        app.MapPost("/site-admin/users/{id:int}/reset-mfa", async (
            int id, HttpContext ctx, UserAdministrationService userAdmin,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SiteAdminMfaReset");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await userAdmin.ResetMfaAsync(id, ct);
                logger.LogInformation("SiteAdmin reset MFA for user {UserId}.", id);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=mfa-reset");
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapGet("/site-admin/backups/{id:int}/download", async (
            int id, HttpContext ctx, BackupService backups, CancellationToken ct) =>
        {
            var opened = await backups.OpenForDownloadAsync(id, ct);
            if (opened is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var (row, stream) = opened.Value;
            try
            {
                ctx.Response.ContentType = "application/octet-stream";
                var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                cd.SetHttpFileName(row.FileName);
                ctx.Response.Headers.ContentDisposition = cd.ToString();
                ctx.Response.ContentLength = row.FileSizeBytes;
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/storage/{orgId:int}/quota", async (
            int orgId, HttpContext ctx, DatabaseUsageService usage, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var raw = form["QuotaMb"].ToString();
            int? quota = string.IsNullOrWhiteSpace(raw)
                ? null
                : int.TryParse(raw, out var parsed) ? parsed : -1;
            if (quota == -1)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Quota must be a non-negative whole number, or blank for the system default."));
                return;
            }
            try
            {
                await usage.SetOrgQuotaAsync(orgId, quota, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.OkQuery}=quota-saved");
            }
            catch (ArgumentOutOfRangeException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        return app;
    }
}
