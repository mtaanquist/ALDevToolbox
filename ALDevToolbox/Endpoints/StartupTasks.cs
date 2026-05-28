using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Runs at startup, after the host has been built: applies pending EF
/// migrations, ensures the singleton system org exists, and creates the
/// bootstrap SiteAdmin from BOOTSTRAP_ADMIN_* env vars when the database is
/// empty. Flips /readyz to green only once everything has finished.
/// </summary>
internal static class StartupTasks
{
    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var stopping = app.Lifetime.ApplicationStopping;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.Account.AuthService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await db.Database.MigrateAsync(stopping);

        // Ensure the Default org exists (covers EnsureCreated paths in tests)
        // and that it carries the IsSystem flag — the migration stamps it for
        // normal boots, but a freshly-created row from the test path still
        // needs it.
        var defaultOrg = await db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Slug == "default", stopping);
        if (defaultOrg is null)
        {
            defaultOrg = new Organization
            {
                Name = "Default",
                Slug = "default",
                IsPending = false,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Organizations.Add(defaultOrg);
            await db.SaveChangesAsync(stopping);
        }
        else if (!defaultOrg.IsSystem)
        {
            defaultOrg.IsSystem = true;
            await db.SaveChangesAsync(stopping);
        }

        // Seed the platform-default workspace files only for orgs that have
        // none yet. Once an org has any files, its admins own that list:
        // re-running a per-path backfill here would resurrect platform
        // defaults they deliberately removed. Genuinely new platform files are
        // propagated to existing orgs via a one-off migration backfill (see
        // BackfillAppJsonAsIncludedFile), not on every boot.
        var allOrgIds = await db.Organizations
            .IgnoreQueryFilters()
            .Select(o => o.Id)
            .ToListAsync(stopping);
        var orgIdsWithFiles = (await db.OrganizationFiles
            .IgnoreQueryFilters()
            .Select(f => f.OrganizationId)
            .Distinct()
            .ToListAsync(stopping))
            .ToHashSet();
        foreach (var orgId in allOrgIds)
        {
            if (orgIdsWithFiles.Contains(orgId)) continue;
            await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(db, orgId, DateTime.UtcNow, stopping);
        }
        await db.SaveChangesAsync(stopping);

        // Bootstrap admin: only runs once, when there are no users in the
        // database. After that the env vars are read but ignored (logged).
        var bootstrapEmail = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_EMAIL");
        var bootstrapPassword = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_PASSWORD");
        var anyUsers = await db.Users.IgnoreQueryFilters().AnyAsync(stopping);
        if (!anyUsers && !string.IsNullOrWhiteSpace(bootstrapEmail) && !string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            var hash = auth.HashPassword(bootstrapPassword);
            db.Users.Add(new User
            {
                OrganizationId = defaultOrg.Id,
                Email = bootstrapEmail.Trim().ToLowerInvariant(),
                DisplayName = "Administrator",
                PasswordHash = hash,
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                // The bootstrap admin is the first hosting operator;
                // subsequent SiteAdmins are promoted from /site-admin/users.
                IsSiteAdmin = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(stopping);
            logger.LogInformation("Bootstrap SiteAdmin {Email} created in Default organisation.", bootstrapEmail);
        }
        else if (anyUsers && (!string.IsNullOrWhiteSpace(bootstrapEmail) || !string.IsNullOrWhiteSpace(bootstrapPassword)))
        {
            logger.LogWarning(
                "BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are set but at least one user already exists. "
                + "These environment variables only take effect on a fresh database; remove them once the bootstrap account is in place.");
        }

        // Reconcile durable import-job rows: re-enqueue URL downloads that were
        // queued or running at restart (the download is idempotent and we have
        // the URL on the row); flip staged-zip jobs to failed because their
        // temp file lives in container-local /tmp and is gone. The reconciler
        // returns the jobs to re-enqueue here; we push them through the queue
        // so the worker picks them up like a fresh submission.
        var persistedJobs = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.PersistedImportJobs>();
        var queue = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.ReleaseImportQueue>();
        var resumable = await persistedJobs.ReconcileOnStartupAsync(stopping);
        foreach (var job in resumable)
        {
            await queue.EnqueueAsync(job, stopping);
        }
        if (resumable.Count > 0)
        {
            logger.LogInformation("Resumed {Count} URL-source release import(s) after restart.", resumable.Count);
        }

        // Belt-and-suspenders: any OeRelease row still left in "ingesting"
        // WITHOUT a re-queued durable job (synchronous individual-file imports
        // that crashed mid-way, or pre-table-existing in-flight rows) gets the
        // generic "interrupted by restart" treatment so the list page doesn't
        // strand them. Excluding releases with a queued job row is important —
        // the reconciler above just re-enqueued those for resume; flipping
        // them to failed here would silently undo the resume. Cross-org by
        // design — same blessed startup-maintenance category as the
        // migration/seed/bootstrap steps above, never a request.
        var resumableReleaseIds = await db.OeImportJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == "queued")
            .Select(j => j.ReleaseId)
            .ToListAsync(stopping);
        var stranded = await db.OeReleases
            .IgnoreQueryFilters()
            .Where(r => r.Status == "ingesting" && !resumableReleaseIds.Contains(r.Id))
            .ToListAsync(stopping);
        if (stranded.Count > 0)
        {
            foreach (var release in stranded)
            {
                release.Status = "failed";
                release.StatusMessage = "Import was interrupted by a server restart. Re-import to try again.";
                release.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stopping);
            logger.LogWarning("Marked {Count} interrupted release import(s) as failed on startup.", stranded.Count);
        }

        // Prime the in-memory MCP toggle from the singleton system_settings
        // row before any request can read it. Resolved from the root provider
        // because McpAvailabilityState is a singleton — see
        // Services/Mcp/IMcpAvailability.cs for why the cache exists.
        var mcpEnabled = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.McpEnabled)
            .FirstOrDefaultAsync(stopping);
        app.Services.GetRequiredService<ALDevToolbox.Services.Mcp.McpAvailabilityState>().Set(mcpEnabled);

        // Flip /readyz to green now that migrations, seed and bootstrap have
        // all run. Resolved from the root service provider so the flag
        // survives the scope's disposal.
        app.Services.GetRequiredService<StartupReadinessState>().MarkReady();
        logger.LogInformation("Startup complete; /readyz is now green.");
    }
}
