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

        // Ensure every org carries the platform-default workspace files.
        // The MovePlatformFilesToOrgFiles migration covered existing orgs at
        // migration time, but new platform files added later (like the
        // canonical per-extension app.json) need a startup-time backfill so
        // existing orgs pick them up too. Idempotent: the seeder skips
        // existing rows by path so reboots are no-ops.
        var allOrgIds = await db.Organizations
            .IgnoreQueryFilters()
            .Select(o => o.Id)
            .ToListAsync(stopping);
        foreach (var orgId in allOrgIds)
        {
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
