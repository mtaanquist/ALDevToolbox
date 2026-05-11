# Architecture

## Stack

- **Blazor Server** for the UI. Server-rendered Razor components with SignalR for interactivity. No SPA build pipeline, no separate frontend repo, deployment is a single .NET app.
- **.NET 9** (or current stable when implementation begins).
- **EF Core** with the SQLite provider as the data layer. Migrations enabled.
- **Tomlyn** for parsing TOML — used *only* during the first-run seed import. Templates and catalogue are not read from TOML at runtime.
- **System.IO.Compression** for building the output ZIP server-side.
- **Tabler icons** or **Lucide.Blazor** for icons. Either is fine; pick one and use it consistently.
- **No client-side framework beyond Blazor itself.** No React, no JS bundler.

## Why these choices

**Blazor Server over Blazor WebAssembly:** This is an internal tool with low concurrency and a tightly scoped audience. Server-side rendering keeps the deploy simple (one binary), avoids WASM payload size issues, and lets the generator run server-side where streaming a ZIP is straightforward. The original tool ran client-side because it had to live on a static page; that constraint is gone.

**SQLite over Postgres or similar:** One file, one volume, no separate database service. Backups are `cp data.db data.db.bak`. For a tool with single-digit concurrent users editing templates rarely, this is all that's needed.

**Templates in the database, not on disk:** Decided after weighing both options. SQLite makes Docker deployment simpler (one volume mount), allows live editing through the admin UI without redeploys, and supports an audit log natively. The cost — losing git history of template edits — is mitigated by the audit log table and an export-to-TOML feature for periodic snapshots. See `templates-and-seeding.md` for the seed strategy that keeps a source-controlled starting point.

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
│  SQLite                                      │  Single .db file
└──────────────────────────────────────────────┘
```

A clean folder structure on disk would mirror this: `Pages/`, `Components/`, `Services/`, `Domain/`, `Data/`. Implementer's discretion.

## Key services

- **`TemplateService`** — read templates and folders, list available templates for the dropdown, get full template detail by key. CRUD operations for the admin UI.
- **`ModuleService`** — read module catalogue, list available modules, CRUD for admin UI.
- **`CatalogService`** — read/edit the well-known-dependencies catalogue used by the New Extension flow.
- **`GenerationService`** — given a `ProjectPlan` (workspace + selected modules + options) and a template, produce a ZIP stream. See `generation-engine.md`.
- **`SeedService`** — runs once on startup if the database is empty. Reads `Templates.seed/` from a configured path and populates the database. Idempotent — does nothing if any templates already exist.
- **`AuditService`** — writes audit log entries. Implemented as an EF Core `SaveChangesInterceptor` rather than service calls scattered through code; see `auth-and-audit.md`.
- **`AuthService`** — validates the shared password against the env var, issues the auth cookie, captures the user's display name.

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

Standard `ILogger<T>` injection. Log:
- Each generation event (template key, modules selected, output size, duration).
- Each authentication attempt (success or failure).
- Each audit log write (at debug level — it's redundant with the table itself).

Don't log sensitive data; the only thing remotely sensitive is the password and that's only ever compared, never logged.
