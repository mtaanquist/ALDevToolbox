# Milestones

A suggested order for building this. Each milestone is meant to be a coherent chunk of work — a single PR or a small handful — that ends with a working, demonstrable thing. Don't optimise for "least code"; optimise for "clearest tested deliverable per step."

## Milestone 1 — Walking skeleton

Goal: the app runs, has the shell layout, and can show static content. Nothing actually works yet, but the structure is in place.

- Create the Blazor Server project.
- Set up the `MainLayout.razor` with the top bar + sidebar + content slot.
- Add the three top-level routes: `/projects/new`, `/projects/extension`, `/templates`. Each is a stub page with a heading.
- Add the sidebar with the Tools / Resources sections, linking to those routes.
- Wire up the icon library (Tabler / Lucide).
- Configure logging to stdout.

**Done when:** you can `dotnet run`, hit the app in a browser, navigate between the three pages, and the layout matches the design in `ui-design.md`.

## Milestone 2 — Persistence and seeding

Goal: the database exists, has the right schema, and gets populated from seed TOMLs on first run.

- Add EF Core + Sqlite + Tomlyn packages.
- Create the `AppDbContext` with all entities from `domain-model.md`.
- Configure migrations; create the initial migration.
- Implement `SeedService` reading from `Templates.seed/`.
- Add startup logic that runs migrations and then runs the seed if the templates table is empty.
- The `/templates` page now actually queries the database and lists what's there (read-only).

**Done when:** running against an empty data dir produces a populated database, and `/templates` shows the seeded templates and modules.

## Milestone 3 — Generation engine, workspace flow

Goal: New Workspace flow works end to end. Skip auth, skip admin section, skip the live preview for now — just get a working ZIP.

- Implement `GenerationService` per `generation-engine.md`.
- Build the New Workspace form with all fields. No live preview yet — just form + Generate button.
- Wire up the Generate button to call `GenerationService` and stream the ZIP back as a file download.
- Embed the static assets (logo, ruleset, .gitignore template) as resources.
- Implement the mustache substitution for example AL files.
- Add example AL files for the seeded templates under `Templates.seed/runtime-*/examples/`. (Implementer or your team to provide actual content based on existing Core extension.)

**Done when:** filling the form and clicking Generate produces a ZIP that, when extracted, looks like the structure documented in `generation-engine.md`. Verify by extracting and opening in VS Code with the AL extension.

## Milestone 4 — Live preview

Goal: the right column on New Workspace updates in real time as the form changes.

- Build the `<FolderTreePreview>` component.
- Wire it to the form state so it re-renders on any change.
- Add the stat cards (extensions / dependencies count).

**Done when:** typing in the workspace name changes the root folder name in the preview; ticking a module adds its folder; toggling "include examples" changes the contents shown under each folder.

## Milestone 5 — New Extension flow

Goal: the second main user flow.

- Build the `<DependencyPicker>` component (catalogue picker + manual entry form).
- Build the New Extension page using `<DependencyPicker>` and the same `<FolderTreePreview>`.
- Extend `GenerationService` to support standalone extension generation.
- Add the post-generation success message with the workspace `folders` snippet.

**Done when:** generating a standalone extension produces the expected single-folder ZIP, and the success page shows the user how to add it to their existing workspace.

## Milestone 6 — Auth

Goal: admin section is gated.

- Add cookie auth, the `/login` page, and the `[Authorize]` attribute on admin routes.
- Implement `ADMIN_PASSWORD` / `ADMIN_PASSWORD_FILE` reading.
- Add the "Sign in" / "Signed in as Bob" indicator in the top bar.
- Add an `/admin` dashboard placeholder.

**Done when:** unauthenticated users can use the generator but not access `/admin`; authenticated users see their display name and reach the dashboard.

## Milestone 7 — Audit log infrastructure

Goal: any future writes are automatically audited.

- Implement the `AuditInterceptor`.
- Register it on the `AppDbContext`.
- Build the `<AuditHistoryPanel>` component (queries audit log for an entity).
- Build `/admin/audit` showing the global log.

This goes in *before* the admin edit pages so all subsequent writes are captured from day one.

**Done when:** any direct DB modification (use seed re-run or a manual SQL test) appears in the audit log with the correct snapshot.

## Milestone 8 — Admin: templates

Goal: admins can list and edit runtime templates through the UI.

- Build `/admin/templates` listing.
- Build `/admin/templates/{key}` edit page including the folder list editor.
- Wire up create / update / soft-delete actions.
- Embed the `<AuditHistoryPanel>` at the bottom of the edit page.

**Done when:** an admin can edit a template's folders, save, and see the change reflected next time they generate a workspace using that template. The audit log shows the change.

## Milestone 8.5 — Files in folders

Goal: admins can add, edit, and remove files inside template folders through the same admin surface. The on-disk `Templates.seed/<runtime>/examples/` tree stops being the runtime source for example content; the DB owns it.

- Add the `template_files` table per `domain-model.md`. New `TemplateFile` entity hung off `TemplateFolder.Files`.
- Migration: schema add for `template_files`; data backfill that walks the on-disk `examples/<example_path>/` directory for every existing `template_folders` row whose legacy `example_path` is set, inserting one `template_file` row per file (UTF-8 text only); column drop for `template_folders.example_path`.
- Update `SeedService` to populate `template_files` from the example directories on first-run seeds. After this milestone, `Templates.seed/runtime-*/examples/` exists only as a bootstrap source.
- Extend `TemplateInput` / `TemplateFolderInput` with a per-folder `Files` list. Validation: relative path, no `..`, no leading slash, unique per folder. Reuse the same reconciliation pattern `ReconcileFolders` already uses.
- Update `GenerationService.WriteExtensionAsync` to emit files from `TemplateFolder.Files` instead of walking the disk. Remove `ResolveExamplesRoot` and the disk-fallback code path. Mustache substitution still runs on `.al` content.
- Update `TemplateTomlMapper` (and `Domain/Seed/FolderSeed`) to round-trip `[[folders.files]]` blocks with `path` and `content`.
- Admin UI: per-folder expandable file editor in `AdminTemplateEdit.razor`. Path input + content `<textarea>` per row, with reorder/remove/add. Pull the file list out into a small `Components/Shared/TemplateFileEditor.razor` so the live preview can read the same data.
- Audit: `template_files` snapshots store `path` + `sha256(content)` only, not the raw content, to keep the log compact.

**Done when:** an admin can open a template, expand a folder, edit an `.al` file's content, save, and the next workspace generation reflects the change. The on-disk `examples/` tree is no longer read at runtime — confirmed by deleting it on a deployed instance and verifying generation still emits the right files.

## Milestone 9 — Admin: modules and catalogue

Goal: same coverage as templates, for the other two editable entity types.

- Build `/admin/modules` list + edit (with dependency sub-form).
- Build `/admin/catalog` flat editor.

**Done when:** admins can fully manage modules and the well-known catalogue without touching the database directly.

## Milestone 10 — Export to TOML

Goal: the snapshot/backup feature.

- Implement `ExportService` walking the DB and serialising back to the TOML structure.
- Add an "Export all" button on `/admin`.
- Test that exported TOML, fed back into a fresh empty database, produces an equivalent state.

**Done when:** export → wipe DB → seed-from-export produces a database whose row contents match the original (modulo timestamps).

## Milestone 11 — Polish

Goal: everything that makes the difference between "works" and "feels good."

- Validation messages on every form field.
- TOML editor error display: render `PlanValidationException.Errors` as a bulleted list rather than a single concatenated string so admins can scan field-keyed messages back to their TOML location, and consider enabling Tomlyn strict mode so unknown keys (typos like `examplee = "..."`) surface as parse errors instead of being silently dropped.
- Confirmation modals on delete.
- Loading states on the Generate button.
- Empty states on every list (no templates? no modules?).
- The "deprecated" toggle UX in the dropdown.
- Health check endpoints.
- A real README at the repo root explaining how to run locally and in Docker.

## Phase 2 — post-v1 follow-ups

These came out of UX review on the live build. They're real wins but each is
heavy enough to deserve its own milestone, so we land them after v1 ships.

### Milestone P2.1 — Pre-selected modules per template

Goal: the New Workspace module list arrives with the "obvious" set already
ticked, so end-users opt **out** of modules they don't want rather than opting
in to every one.

- Domain: add a many-to-many between `runtime_templates` and `modules`
  (`runtime_template_default_modules`, ordered) capturing "ship this template
  with these modules pre-selected." Migration + EF mapping. Audit it like
  every other relation.
- Admin: a multi-select picker on the template edit page (structured form
  + TOML) that writes the relation. Reuse the `<DependencyPicker>` shape if
  it fits; otherwise factor out a thin `<ModuleMultiSelect>` shared component.
- Seed: extend `template.toml` with a `default_modules = ["foundation", …]`
  array and round-trip it through `TemplateTomlMapper` and `SeedService`.
- New Workspace: hydrate `_selectedModuleKeys` from the picked template's
  defaults on first paint and on template-changed; honour the user's
  subsequent toggles unchanged.

**Done when:** picking a template with default modules ticks them
automatically; switching templates retunes the selection; the admin can edit
defaults via either form or TOML and the change survives a round-trip.

### Milestone P2.2 — Real TOML editor

Goal: the admin TOML view stops being a plain `<textarea>`. Syntax
highlighting, gutter line numbers, error marks at the offending line.

- Pick a small editor: CodeMirror 6 (TOML mode) is the obvious candidate;
  Monaco is heavier than we want. Bring it in via a `.razor.js` companion file
  on `AdminTemplateEdit.razor` so the rest of the app stays JS-bundler-free.
- Render the existing `_fieldErrors` as gutter marks tied to TOML line numbers
  where we can map them; fall back to the bulleted list otherwise.
- Match the app's light/dark theme via the existing `ThemeToggle` signal.

**Done when:** opening the TOML tab loads the editor with syntax colours,
typing produces clear feedback on parse errors, and the dark-mode theme
follows the rest of the app.

### Milestone P2.3 — Workspace config save & re-import

Goal: a workspace generated today can be regenerated tomorrow with the same
settings, and a sibling extension can be authored against the same shape.

- New Workspace: include a `workspace.aldt.toml` (or similar) at the workspace
  root capturing template key, brief/description, ID ranges, application/
  runtime versions, and the module selection. Same shape as the form post.
- New Workspace + New Extension: an "Import config" action that accepts the
  saved file, hydrates the form, then lets the user generate from there.
  Validate against the live database (the chosen template/modules must still
  exist and not be deleted) before populating the form.
- Round-trip via the same `ProjectPlan` / `StandaloneExtensionPlan` records;
  no new domain types unless the test forces it.

**Done when:** generate → save the included config file → import it on a fresh
session → identical ZIP back. New Extension can pick up the same config and
scaffold a sibling extension that lines up with the workspace's ID ranges and
publisher.

### Milestone P2.4 — Application-version catalogue

Goal: Application Version and Runtime stop being free-text inputs. They become
selects backed by an admin-managed list with friendly names, and picking an
application version automatically sets the matching runtime.

- Domain: new `application_versions` table with `key`, friendly `name`
  (e.g. "Business Central 2026 Release Wave 1"), `application` (four-part
  Major.Minor.Build.Revision), `runtime` (string like "28.0", since runtimes
  also have minor versions in real BC releases — e.g. "15.2"), `deprecated`,
  `deleted_at`. Audit it like the rest.
- Persistence: drop the regex validators on free-text application/runtime
  inputs; in their place, store a foreign key from the runtime template's
  *defaults* to the application-version row, so each template's default
  preselects an entry rather than carrying its own raw string.
- Admin: `/admin/application-versions` list + edit, mirroring the catalogue
  editor's shape. Reuse the same reconciliation pattern (`Id`-keyed rows,
  audit on real changes only).
- Builder forms: replace the two text inputs on New Workspace and New
  Extension with a single Application-Version select. Picking a row
  populates both `ApplicationVersion` and `RuntimeVersion` from the catalogue
  entry — no separate Runtime input. Keep a small caption underneath that
  shows the resolved versions in code style so users still see what they're
  about to ship.
- Seed: extend `template.toml` with `default_application_version = "<key>"`
  on the `[template]` table; `Templates.seed/application-versions/*.toml`
  bootstraps the well-known list (BC v22 → BC v28 etc.). Round-trip through
  `TemplateTomlMapper` and `ExportService`.
- Migration: backfill from the current `default_application` /
  `default_platform` columns by deriving runtime from the existing
  `runtime` column; orphan templates without a match keep raw strings until
  an admin assigns one.

**Done when:** an admin can curate the version list with friendly labels;
picking an entry on the builder forms fills both fields atomically; seed and
export round-trip; the audit log captures version-table changes.

## Out of scope for v1

These remain off the table — listed here so they don't get pulled into a current milestone by accident. Phase 4 candidates in `milestones.md` revisit some of these.

- Mobile-friendly layout. Desktop only.
- A structured editor for `defaults_json` and `app_source_cop_json`. Textarea is fine for v1.
- Automatic migration testing in CI. Manual is fine for v1.
- Cross-organisation superuser. There's no support / debugging account that bypasses the org filter.
- SSO / OIDC, two-factor, magic-link login, invite-by-email. Email + password is the only path.
- Binary files inside template folders. Text content only.

(Multi-tenancy and per-user accounts moved on-scope and shipped in Phase 3 — M13 added organisations and accounts; M14 added per-org configuration.)

## Deliberately small

A few decisions throughout the design exist to keep this small. If you find yourself building something that feels disproportionately complex, check that you're not over-engineering one of these:

- SQLite instead of Postgres.
- One container, one volume.
- Synchronous generation (no queue).
- TOML is an authoring format on top of the DB, never a peer persistence path. `Templates.seed/` bootstraps an empty *organisation* (templates, modules, catalogue, per-folder file contents, organisation defaults, logo, always-included files); nothing watches it or writes back to it.
- JSON columns for `defaults` and `app_source_cop` instead of normalised tables.
- Admin UI edits structured data and TOML; AL file contents stay in the repo's seed folder until an admin first edits them.
- Three roles (`User`, `Editor`, `Admin`); admin-approved signups; no superuser. (`Editor` was added after M13 as a content-authoring role between `User` and `Admin`.)
