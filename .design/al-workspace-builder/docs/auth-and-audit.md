# Auth and audit

## Auth model

A single shared admin password. Anyone who knows it can edit templates, modules, and the catalogue. Anyone without it can use the project generator and view templates read-only.

This is deliberately unsophisticated. The intended audience is a small internal team where 2–3 people are trusted to edit. If user demand for proper accounts ever arrives, the auth abstraction (described below) makes swapping in a real IdP a localised change.

## Login flow

`/login` page presents:

- Password (single field, required, type=password).
- Display name (text, required) — the honour-system "who am I" capture for the audit log. e.g. "Bob", "Alice — Engineering". No validation beyond non-empty.
- "Sign in" button.

On submit:

1. Validate the password against `ADMIN_PASSWORD` (env variable). Use a constant-time comparison.
2. If correct, create a signed cookie containing the display name and an expiry (default 8 hours).
3. Redirect to the page the user was originally trying to reach (or `/admin` if none).

If incorrect, show "Invalid password" and stay on `/login`. Don't reveal whether the password was missing or wrong.

## Cookie

Use ASP.NET Core's cookie authentication scheme. The cookie payload is just the display name as a single claim; the *fact* of having a valid cookie means the user is the admin. There's no role hierarchy to encode.

```csharp
services.AddAuthentication("Cookie")
    .AddCookie("Cookie", options => {
        options.Cookie.Name = "alwb_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });
```

The cookie is never the password. The password is only ever held in memory long enough to compare. Don't store the password in the cookie even if it would simplify things; it would also expose the password to anyone with browser access.

## Protecting routes

In Blazor Server with Razor components, `@attribute [Authorize]` on the admin pages is the simplest approach. Combined with the cookie scheme above, unauthenticated requests are redirected to `/login`.

Apply `[Authorize]` to:

- All pages under `/admin/*`
- Any controller endpoints that mutate state (template create/update/delete, module create/update/delete, catalogue mutations, export-to-TOML).

The generation endpoints stay unauthenticated — generating workspaces is the main public capability of the tool.

## Audit log

Every mutation to a template, template_folder, module, module_dependency, or well_known_dependency writes an audit log row. The infrastructure is a single `SaveChangesInterceptor`:

```csharp
class AuditInterceptor : SaveChangesInterceptor {
    private readonly IHttpContextAccessor _http;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var ctx = eventData.Context!;
        var auditedTypes = new[] { typeof(RuntimeTemplate), typeof(TemplateFolder),
                                    typeof(Module), typeof(ModuleDependency),
                                    typeof(WellKnownDependency) };

        var changedBy = _http.HttpContext?.User?.Identity?.Name ?? "unknown";

        var entriesToAudit = ctx.ChangeTracker.Entries()
            .Where(e => auditedTypes.Contains(e.Entity.GetType())
                        && e.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entriesToAudit)
        {
            var snapshot = SerializeOriginalValues(entry);
            ctx.Add(new AuditLogEntry {
                Timestamp = DateTime.UtcNow,
                ChangedBy = changedBy,
                EntityType = entry.Entity.GetType().Name,
                EntityId = (int)entry.Property("Id").CurrentValue!,
                Action = entry.State == EntityState.Modified ? "updated" : "deleted",
                SnapshotJson = snapshot
            });
        }

        // For added entries, we capture them after they have IDs assigned
        // (so this needs a second pass — see below)

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
```

Notes on the implementation:

- **Modified and deleted entities:** capture `OriginalValues` *before* the save. This is the snapshot of the row as it existed before this change.
- **Added (created) entities:** capture happens in `SavedChangesAsync` (the post-save callback) so we have the assigned ID. Snapshot is null for created entries — there's nothing "before" to record. The fact of creation is captured by the row itself.
- The interceptor is registered in DI: `services.AddDbContext<AppDbContext>(opts => opts.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>()))` (or registered on the DbContext directly — implementation detail).

## What gets snapshotted

For each audited entity, the snapshot JSON includes *all* of its persisted columns plus a stable representation of its dependent collections. Specifically:

- A `RuntimeTemplate` snapshot includes its scalar columns plus an inline array of its `template_folders` rows (their state at save time).
- A `Module` snapshot includes its scalars plus its `module_dependencies`.
- Standalone child rows (a `template_folder` edited directly without touching its parent template) snapshot just themselves.

This is so an admin investigating "what did Document Capture look like in March" can see the full module with its deps in one snapshot, not piece it together across rows. EF Core change tracking can give you this — walk the principal's owned/related collection state via `entry.Collections`.

## Audit retention

No automatic deletion. The audit log grows forever. For this volume of edits (a few per month, optimistically), a few thousand rows over the app's lifetime is fine. If it ever becomes a problem, a manual cleanup query is acceptable.

## What is *not* audited

- Workspace generations. They don't mutate database state, and their inputs are user-supplied so there's nothing meaningful to recover. `ILogger` records them at Info level, which is enough for "did anyone generate something at 3pm."
- Login attempts. They go to the standard log. Successful logins create a session; failed logins don't write to any DB table.
- Reads. No "who viewed what" tracking.

## Error handling and integrity

- If the audit interceptor fails (JSON serialization throws), the entire `SaveChanges` should fail and roll back. The premise is that we'd rather refuse to make a change than make it without a record.
- If the audit log table itself is corrupted or unreachable, the app should fail loudly at startup, not silently disable auditing.
