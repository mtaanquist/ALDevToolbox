# Auth and audit

## Auth model

Email/password accounts scoped to organisations. Three roles:

- `User` — can use the project generator.
- `Editor` — additionally sees the content-authoring admin pages (templates, modules, catalogue, cookbook, application versions, object explorer including releases import), but **not** the Administration tab, the Admin dashboard, the audit log, or the storage-quota footer.
- `Admin` — full control of the organisation, including users, invites, identity, MCP, OAuth clients, export, audit, and content authoring.

There is no superuser; an admin only ever sees their own organisation. The "last active admin" guard in `UserAdministrationService.ChangeRoleAsync` blocks any demotion away from `Admin` (to `Editor` or `User`) when the row is the sole active admin in the org — Editors do not count as admins for that guard.

**Multi-tenancy.** Every editable entity carries an `organization_id`. EF Core query filters on `AppDbContext` scope reads to the acting user's organisation; the only paths that bypass them with `IgnoreQueryFilters()` are the ones that *can't* know an organisation yet (login, signup, password reset, bootstrap). Cross-org reads from a signed-in session return nothing — verified by `CrossOrgIsolationTests`.

**Bootstrap.** A fresh deployment creates the "Default" organisation through the M13 migration and stamps it as the singleton **system org** (`organizations.is_system = true`) via the `MoveSeedToSystemOrg` migration. The system org hosts the canonical templates other organisations fork from; it starts empty until a SiteAdmin authors content via `/admin/templates`. The first admin user is created from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars on first boot. After at least one user exists those variables are read and warned about (so a stale value in production logs gets surfaced) but never re-applied.

**New organisations.** Signups against a blank or unknown slug **auto-approve**. The new `Organization` is created (with `is_system = false`), the user is stored as `Active Admin` of that org, the cookie is issued, and the user lands on the home page in one step. The org starts empty — no templates, no modules, no catalogue. Admins populate it on demand by forking from the system catalogue via the "From the site catalogue" section of `/admin/templates`, or by authoring directly. We deliberately have no per-org superuser (the cross-org `SiteAdmin` operator role from M17 manages deployments, not individual orgs' data), so requiring approval here would leave new orgs unreachable. A `SignupRequest` row is still written with `Decision=Approved, DecidedByUserId=<self>` so `/admin/users` retains a complete history. Operators who don't want anyone to provision new orgs should hide the `/signup` route at the proxy or set up their deployment with `BOOTSTRAP_ADMIN_*` and never advertise public signup.

**Existing-org signups** still go through admin approval in `/admin/users` and email notification. Admin-issued invites shipped in M19: an admin creates one at `/admin/administration/users/new`, the invitee receives an email (when SMTP is configured) or an inline link, and redeems it at `/accept-invite` — setting a password and joining the org `Active`. Invite tokens are stored as sha-256 hashes with a 7-day single-use expiry (`InviteService`). The older "ask the user to sign up with your slug and approve them" path still works for self-service.

**Email-first verified signup.** When SMTP is configured, `/signup` runs an email-first, verified flow instead of the single form above. Step 1 collects only an email and `POST /auth/signup/start` persists a `PendingSignup` (org-less, read via `IgnoreQueryFilters()` like `Invite`/`PasswordResetToken`) and emails a one-time link **and** a 6-digit code (`PendingSignupService`; link token via `TokenIssuer`, code hashed bound to the row's link-token hash, both 30-minute lifetime). The response is always the same generic "check your email" — it never reveals whether the address is new, already registered (no row, no email sent), rate-limited, or domain-disallowed. Step 2 verifies via `GET /auth/signup/verify` (link) or `POST /auth/signup/verify-code` (code), which stamps `verified_at` and sets a short-lived Data-Protected verified-email cookie. Step 3 (`/signup/details`, `POST /auth/signup/complete`) re-resolves the verified email **server-side** from a verified, uncompleted, unexpired row — the cookie is only a hint, so a forged cookie is inert — and branches: a claimed-domain email joins that org (Active immediately when the org has `OrganizationSettings.AutoJoinVerifiedDomainUsers` on, otherwise Pending for admin approval, the historical behaviour), and an unclaimed-domain email creates a brand-new org with the visitor as its Active Admin. There is no "join an arbitrary org by typing its slug" path in this flow. The verification row's `completed_at` is the single-use guard. When SMTP is **not** configured the original single-form `/auth/signup` flow is used unchanged, so a zero-config deployment can still get started. Mandatory two-factor is not bolted onto signup: `StrongAuthGate` already redirects a newly-Active member of a `RequireStrongAuth` org to `/account?required=1` on their next request.

## Pages

| Path | Audience | Purpose |
|------|----------|---------|
| `/login` | Anonymous | Email + password sign-in. |
| `/signup` | Anonymous | Email-first verified signup when SMTP is configured (verify, then name + password, plus org name/slug for a new org); falls back to the single all-fields form when SMTP is off. |
| `/signup/details` | Anonymous | Step 3 of the verified flow: collects the remaining fields for a verified email (gated by the verified-email cookie + a server-side `PendingSignup` re-check). |
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

The cookie carries the user's id, organisation id, role, display name and email as claims. `HttpOrganizationContext` reads `org_id` and `user_id` to drive EF query filters; the rest are for the top-bar caption ("Bob (CRONUS) — Admin") and audit attribution.

## Bearer credentials for /mcp

The MCP endpoint accepts two bearer flavours via the `McpBearer` authorisation policy. Both schemes mount the same downstream claim shape, so MCP tools see identical principals regardless of which credential authenticated the request.

* **Personal access tokens** (`Services/Account/PatAuthenticationHandler.cs`) — `aldt_pat_…` opaque tokens, SHA-256 hashed at rest, scoped to (user, organisation). The desktop/CLI path: Claude Desktop, Claude Code, Cursor, VS Code Copilot agent mode all paste one into a config file. Issued from `/account?section=ai`, revoked from the same page or from SiteAdmin oversight.
* **OAuth 2.1 access tokens** (OpenIddict server at `/oauth/*`, validation via `Services/OAuth/OAuthClaimsTransformer.cs`) — for clients that don't accept a pasted credential. Today that's Claude.ai on the web, Claude mobile, and Claude Cowork; their directory and custom-connector flow does Dynamic Client Registration (RFC 7591) on first connect, then the user clicks Allow on a consent screen at `/oauth/authorize`. The flow and table layout are documented in `.design/mcp-oauth.md`.

On a 401 from `/mcp`, the PAT handler emits `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"` so Claude can discover the OAuth server. The MCP spec only honours this discovery pointer on a real 401 — the per-handler challenge override in `PatAuthenticationHandler.HandleChallengeAsync` is where the contract lives.

The OpenIddict EF Core tables (`oauth_applications`, `oauth_authorizations`, `oauth_scopes`, `oauth_tokens`) are intentionally **outside** the multi-tenant query filter; pre-auth flows (`/oauth/token`, `/oauth/register`) have to read them before any organisation context exists. Org attribution lives in OpenIddict's free-form `properties` JSON column and is stamped during the consent step. `oauth_consents` is ours and is inside the standard filter.

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
- **Two-factor authentication** — TOTP (authenticator app) is the primary factor; email codes are an optional fallback. Both can be enrolled at once.
- **Passkeys** — WebAuthn credentials registered via `navigator.credentials.create`. A successful passkey assertion at login is full authentication; it replaces both the password and the 2FA challenge.
- **Delete account** — removes the user row. Refined guard: if the user is the last active admin **and** there are other members in the org, the form refuses outright and tells them to promote someone first. If they are the only member, the confirmation accepts cascade-the-org. See `AccountService.DeleteAccountAsync`.

Email change for a user is performed by another admin (not by the user themselves in this milestone). See "Admin user management" below.

## Two-factor authentication

### TOTP
- 20-byte secret, Base32-encoded, encrypted via `IDataProtector` (purpose `ALDevToolbox.UserTotpSecret`) before persistence. Decryption only happens inside `TotpService.VerifyAsync` to compute the rolling code.
- Stored in `user_totp_secrets` (one row per user). A row with `confirmed_at = null` is a pending enrollment that hasn't yet been validated by submitting a current code — repeating enrollment overwrites it.
- Confirmation flips `users.totp_enabled = true` and issues 10 recovery codes.
- The Data Protection key ring lives on the `app-keys` volume; losing it makes TOTP secrets unrecoverable (same blast radius as the SMTP password). Recovery: email-MFA fallback, recovery codes, or `UserAdministrationService.ResetMfaAsync` (the SiteAdmin break-glass path). The OAuth signing + encryption keys (`oauth-signing.key`, `oauth-encryption.key`, written by `Services/OAuth/OAuthKeyMaterial.cs`) sit in the same directory; losing them additionally forces every connected OAuth client to re-consent on its next request, but does not affect cookie sign-ins or PATs.

### Email codes
- 6-digit numeric, single-use, 10-minute lifetime. Stored hash is `SHA-256(code + ":" + user_id)`; the user-id binding stops the short numeric space being a global rainbow-table target.
- Reuses the `password_reset_tokens` table with `purpose = EmailMfaChallenge`. Per-user rate limit: 3 issues per 10 minutes, counted from that same table — survives process restart with no extra state.
- Always sent to `users.email`, never `pending_email`, so a still-unconfirmed email change can't redirect MFA codes to the new address.

### Recovery codes
- 10 codes per user, two five-char groups (`7HK3M-2QPRA`), drawn from an unambiguous alphabet. BCrypt-hashed (work factor 10).
- Shown once, immediately after TOTP enrollment or after regenerate. Regenerating wipes all prior codes.

### Login orchestration
- `AuthService.TryLoginAsync` returns `LoginOutcome.MfaRequired` once the password verifies and the user has any 2FA enrolled. `LastLoginAt` and the login-success row in `login_attempts` are deferred to `AuthService.CompleteMfaAsync`, called by the challenge endpoint after the second factor verifies.
- The intermediate "password OK, 2FA pending" state lives in a short-lived signed cookie (`alwb_mfa`, 10 min, `IDataProtector` purpose `ALDevToolbox.MfaPending`). No server-side session table.
- Magic-link login deliberately bypasses 2FA — the mailbox itself is treated as a second factor.

## Passkeys (WebAuthn)

- Backed by `Fido2.AspNet`. Configuration: `Auth:WebAuthn:RpId` (registrable suffix of the deployed hostname) and `Auth:WebAuthn:Origins` (allow-listed full origins). When `RpId` is empty the passkey UI hides and the service refuses to start ceremonies — set both before exposing passkeys publicly.
- `user_passkeys` holds the credential id (globally unique), CBOR-encoded public key, sign counter, AAGUID, nickname, and transport hints. One user can register many.
- Registration / assertion challenges round-trip via short-lived (5 min) `IDataProtector`-protected cookies; no extra state table.
- A non-monotonic sign counter (signal of a cloned authenticator) refuses the sign-in.

## Admin user management

`/admin/users` (Admin-only):

- **Create a user** — generates an invite without requiring SMTP; the magic link is shown inline once with a copy-to-clipboard button (via a 60s `IDataProtector`-protected cookie). If SMTP is configured and the admin ticked "Send email", the invite is also emailed.
- **Invite by email** — existing flow, kept for backwards compat.
- **Approve / reject pending signups** — unchanged.
- **Disable / re-enable / change role** — unchanged; last-admin guards on demote/disable.
- **Change a user's email** — stamps `users.pending_email` + `pending_email_at`, issues a 24-hour `EmailChangeConfirm` token via `TokenIssuer.Issue`, and sends the confirmation link to the **new** address. If SMTP isn't reachable the link is also shown inline. The swap happens when the new mailbox clicks the link (`/auth/account/email-change/confirm`), at which point the user is signed out so they re-login from the new address. An admin cannot change their own email via this path.

`/site-admin/users/{id}/reset-mfa` is the break-glass path for users locked out of every factor: clears the TOTP secret, every recovery code, every passkey, and flips both MFA flags off. Audited.

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
