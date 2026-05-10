# Auth and audit

## Auth model

Email/password accounts scoped to organisations. Two roles: `User` (can use the project generator) and `Admin` (can manage templates, modules, catalogue, application versions, audit log, and other users in the same organisation). There is no superuser; an admin only ever sees their own organisation.

**Multi-tenancy.** Every editable entity carries an `organization_id`. EF Core query filters on `AppDbContext` scope reads to the acting user's organisation; the only paths that bypass them with `IgnoreQueryFilters()` are the ones that *can't* know an organisation yet (login, signup, password reset, bootstrap). Cross-org reads from a signed-in session return nothing — verified by `CrossOrgIsolationTests`.

**Bootstrap.** A fresh deployment creates the "Default" organisation through the M13 migration, then seeds it with the standard `Templates.seed/` content. The first admin user is created from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars on first boot. After at least one user exists those variables are read and warned about (so a stale value in production logs gets surfaced) but never re-applied.

**New organisations.** Signups against an unknown slug create a *pending* organisation. The signup itself is also pending — but because the new org has no admins yet, there's no one in-system to approve it. In practice the bootstrap admin (or, in a hosted multi-org future, a `SiteAdmin` role) does the cross-org approval. Once the first admin signs in, `SeedService.RunAsync(orgId)` runs against that new org and `IsSeeded` flips to true.

## Pages

| Path | Audience | Purpose |
|------|----------|---------|
| `/login` | Anonymous | Email + password sign-in. |
| `/signup` | Anonymous | Email, display name, password, optional org slug. Always lands in `Pending` status. |
| `/forgot-password` | Anonymous | Email a reset link if SMTP is configured. Always shows the same "if that email exists" message. |
| `/reset-password?token=…` | Anonymous | Single-use; expires after one hour. |
| `/account` | User+ | Self-service: change password / display name, delete account. |
| `/admin/users` | Admin | Approve / reject pending signups, change roles, disable users in the same org. |

End-user generator pages (`/projects/new`, `/projects/extension`, `/templates*`) are now `[Authorize]` — anonymous users redirect to `/login` with a `return=` query.

## Cookie

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

The cookie carries the user's id, organisation id, role, display name and email as claims. `HttpOrganizationContext` reads `org_id` and `user_id` to drive EF query filters; the rest are for the top-bar caption ("Bob (Acme) — Admin") and audit attribution.

## Password hashing

BCrypt with a work factor of 12 via `BCrypt.Net-Next`. We picked BCrypt over Argon2id for two reasons: (1) the `BCrypt.Net-Next` package is mature, ships pre-built, and has no native dependencies; (2) at our scale and with cookie-based sessions the BCrypt vs Argon2id practical difference is negligible. If the threat model changes, the swap is local to `AccountService.HashPassword` / `VerifyPassword`.

Password policy: minimum 12 characters; no other rules. Length beats classes.

## Login hardening

- **Per-email rate limit**: max 10 attempts per 15 minutes.
- **Per-IP rate limit**: max 30 attempts per 15 minutes.
- **Lockout**: five consecutive failures with no intervening success locks the account for 15 minutes. Successful sign-in clears the streak.
- **Forgot-password rate limit**: same per-email and per-IP windows so the SMTP relay isn't a spam vector. The response is identical regardless of whether the email is known.
- **Reset tokens** are stored as `sha256(token)`, expire after 1 hour, and are single-use (`consumed_at` is stamped on first use).

Every login attempt — successful or not — writes a row to `login_attempts` keyed on email and IP. That table powers both the rate limit windows and the lockout query, against an injectable `TimeProvider` so tests can advance the clock without sleeping.

## Approvals and emails

When a signup arrives, `EmailService` (MailKit) emails every active admin in the target org. When an admin decides, the requester gets a one-line "approved" or "declined" email. SMTP is configured via env vars: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD_FILE`, `SMTP_FROM`, `SMTP_USE_STARTTLS`. If any of those are missing, the page shows "Email is not configured; ask an admin." rather than swallowing the failure — fail loudly so misconfiguration is visible.

Email send failures log a warning but do not roll back the underlying action. A failed approval email shouldn't unapprove the user.

## Account self-service

`/account`:

- **Change password** — requires the current password.
- **Change display name** — 2–80 chars.
- **Delete account** — removes the user row. If the user is the last *active* admin, the confirmation modal asks them to either promote another user first or accept that the organisation will be marked for deletion (which cascades to its content via FK).

## Audit log

Every mutation to a template, template_folder, template_file, template_module_folder, template_module_file, runtime_template_default_module, module, module_dependency, well_known_dependency, application_version, user or signup_request writes a row through `AuditInterceptor`.

`audit_log.changed_by` is now `"display_name <email>"` of the acting user (or `"unknown"` for seed-time inserts that pre-date a signed-in user). `changed_by_user_id` and `organization_id` are stored as separate columns for queryability — `(organization_id, timestamp)` is the index that powers `/admin/audit`.

Snapshot rules unchanged from the original implementation:

- **Modified / Deleted**: snapshot `OriginalValues`.
- **Added**: snapshot is null (there's no "before"); written in `SavedChangesAsync` so the assigned id is in the audit row.
- Principal entities (`RuntimeTemplate`, `TemplateFolder`, `TemplateModuleFolder`, `Module`) inline their child collection's pre-save state.
- `TemplateFile` / `TemplateModuleFile` content is replaced with a SHA-256 hash to keep the audit log compact.

## What is *not* audited

- Workspace / extension generations — `ILogger` at Info level is enough.
- Reads.
- Login attempts (they go to `login_attempts`, which is not the audit log).

## Error handling

- Audit interceptor failure → `SaveChanges` rolls back. We'd rather refuse a change than apply it without a trace.
- A corrupted audit log table fails the app at startup — `MigrateAsync` tries to apply the schema and surfaces any drift.
