# Architecture

## Stack

- **Blazor Server** for the UI. Server-rendered Razor components with SignalR for interactivity. No SPA build pipeline, no separate frontend repo, deployment is a single .NET app.
- **.NET 10**.
- **EF Core 10** with the Npgsql (PostgreSQL) provider as the data layer. Migrations enabled. PostgreSQL 18 is the only supported database from Milestone P4.16 onward.
- **Tomlyn** for parsing TOML — used during the first-run seed import per organisation, by the TOML authoring surface in the admin UI, and by the export/import round-trip. Generation reads from the database, not from TOML.
- **System.IO.Compression** for building the output ZIP server-side.
- **MailKit** for outbound email (signup notifications, password reset).
- **BCrypt.Net-Next** for password hashing.
- **Lucide.Blazor** for icons.
- **No client-side framework beyond Blazor itself.** No React, no JS bundler.

## Why these choices

**Blazor Server over Blazor WebAssembly:** This is an internal tool with low concurrency and a tightly scoped audience. Server-side rendering keeps the deploy simple (one binary), avoids WASM payload size issues, and lets the generator run server-side where streaming a ZIP is straightforward. The original tool ran client-side because it had to live on a static page; that constraint is gone.

**PostgreSQL over SQLite (P4.16):** v1 shipped on SQLite for the simplicity of a single file and a single volume. P4.16 swaps that for `postgres:18-alpine` running as a sibling container in compose, sharing a named volume. The change pays off in three places: tests run against the same engine production uses (no SQLite-vs-Postgres semantic gaps for jsonb / timestamptz / DDL), backups go through `pg_dump` (the foundation of the M18 backup tooling), and the M14 SQLite-specific `__ef_temp_*` table rebuilds disappear from the migration history. The "no external services" architectural fence is intentionally relaxed in spirit: still one app container, but now also one db container and a named volume per concern (data, keys, backups). See `migrating-from-sqlite.md` for the upgrade path.

**Templates in the database, not on disk:** Decided after weighing both options. Storing the templates in Postgres makes Docker deployment simpler (one mount per concern), allows live editing through the admin UI without redeploys, and supports an audit log natively. The cost — losing git history of template edits — is mitigated by the audit log table and an export-to-TOML feature for periodic snapshots. See `templates-and-seeding.md` for the seed strategy that keeps a source-controlled starting point.

## Layers

```
┌──────────────────────────────────────────────┐
│  Razor Pages / Components                    │  Pages, layouts, forms
├──────────────────────────────────────────────┤
│  Application Services                        │  GenerationService,
│                                              │  TemplateService,
│                                              │  ModuleService,
│                                              │  AuditService,
│                                              │  AuthService
├──────────────────────────────────────────────┤
│  Domain                                      │  Entities, value objects,
│                                              │  ProjectPlan, validation
├──────────────────────────────────────────────┤
│  Persistence (EF Core DbContext)             │  Repositories or direct
│                                              │  DbContext use
├──────────────────────────────────────────────┤
│  PostgreSQL 18                               │  Sibling compose service,
│                                              │  named volume `pg-data`
└──────────────────────────────────────────────┘
```

The folder structure mirrors this: `Components/`, `Services/`, `Domain/`, `Data/`. See `CLAUDE.md` for what belongs where.

## Key services

- **`TemplateService`** — read templates and folders, list available templates for the dropdown, get full template detail by key. CRUD operations for the admin UI.
- **`ModuleService`** — read module catalogue, list available modules, CRUD for admin UI.
- **`CatalogService`** — read/edit the well-known-dependencies catalogue used by the New Extension flow.
- **`ApplicationVersionService`** — read/edit the curated AL application versions used to populate the New Workspace and New Extension dropdowns.
- **`GenerationService`** — given a `ProjectPlan` (workspace + selected modules + options) and a template, produce a ZIP stream. See `generation-engine.md`.
- **`TemplateImportService`** — fork pipeline: copies a template (plus its referenced modules and default application version) from the singleton system org into the acting org. Wired to the "From the site catalogue" section of `/admin/templates`. The on-disk `Templates.seed/` bootstrap was retired in favour of this — fresh orgs start empty and import on demand.
- **`OrganizationConfigService`** — reads and writes per-org settings (default publisher, default ID range, default brief / core description), the org logo, and the always-included files admins want appended to every generated workspace.
- **`AuditService`** — read side. Mutations to the audit log happen via the `AuditInterceptor` (EF Core `SaveChangesInterceptor`); see `auth-and-audit.md`.
- **`AccountService`** — sign-in, signup, password reset, role / status changes, login-attempt rate limiting and lockout. Uses `BCrypt.Net-Next` for hashing.
- **`SmtpEmailService`** — outbound mail for signup notifications and password reset. Resolves SMTP from `SystemSettingsService` first, falls back to `SMTP_*` env vars; pages report "Email is not configured" rather than failing silently.
- **`SystemSettingsService`** — reads and writes the singleton `system_settings` row (SMTP override, system banner, default signup approval policy). SMTP password is encrypted at rest via ASP.NET Core Data Protection; the audit interceptor redacts the column.
- **`SiteAdminService`** — cross-organisation operations for hosting operators: search users by email, promote/demote SiteAdmin, audit search across orgs. Uses `IgnoreQueryFilters()` explicitly; mutations guard on `RequireSiteAdmin()`.
- **`ExportService`** — builds the TOML export ZIP downloaded from `/admin/configuration`.
- **`HttpOrganizationContext`** — request-scoped `IOrganizationContext` that pulls `user_id` and `org_id` from the auth cookie's claims; drives EF query filters in `AppDbContext`.

## Request flow examples

**Generating a workspace:**

```
User submits form
  → New Workspace page collects ProjectPlan from the form state
  → GenerationService.Generate(plan, templateKey)
      → loads template + folders from TemplateService
      → loads selected modules from ModuleService
      → builds a virtual file tree in memory
      → writes everything to a MemoryStream as ZIP
  → page returns the stream as a file download response
```

**Editing a template:**

```
Admin submits edit form
  → TemplateService.Update(template)
      → DbContext.Update(...)
      → DbContext.SaveChangesAsync()
          → AuditInterceptor catches the SaveChanges
          → walks ChangeTracker, writes audit_log rows
          → original SaveChanges proceeds
  → admin redirected back to template list
```

## Concurrency

This is a small internal tool. Don't over-engineer:

- Last-write-wins on template edits is fine. If two admins edit the same template at the same time, one of them clobbers the other and the audit log records both events. Add a `RowVersion` column later if it ever actually matters.
- The generation flow is read-only against the database and runs entirely in memory. No need for transactions beyond what EF Core does by default.

## Hot reload

Not needed at runtime — templates live in the database and are read every request (or with a short in-memory cache). The "no redeploy to change a template" property comes for free.

## Logging

Standard `ILogger<T>` injection with structured (named-placeholder) messages. Log:
- Each generation event (template key, modules selected, output size, duration).
- Each authentication attempt (success or failure).
- Each audit log write (at debug level — it's redundant with the table itself).

Don't log sensitive data; passwords are only ever hashed and compared, never logged.
