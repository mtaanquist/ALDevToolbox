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
- Confirmation modals on delete.
- Loading states on the Generate button.
- Empty states on every list (no templates? no modules?).
- The "deprecated" toggle UX in the dropdown.
- Health check endpoints.
- A real README at the repo root explaining how to run locally and in Docker.

## Out of scope for v1

These are mentioned in the design but should not be implemented in the initial build. They're listed here so they don't get pulled in by accident:

- Mobile-friendly layout. Desktop only.
- Per-user accounts / roles. Single shared password is fine.
- A structured editor for `defaults_json` and `app_source_cop_json`. Textarea is fine for v1.
- An in-app diff viewer for audit snapshots. Showing the JSON is fine for v1.
- Automatic migration testing in CI. Manual is fine for v1.
- Editing example AL file *contents* through the UI. They stay as repo files.
- Multi-tenancy.

## Deliberately small

A few decisions throughout the design exist to keep this small. If you find yourself building something that feels disproportionately complex, check that you're not over-engineering one of these:

- Single shared password instead of accounts.
- SQLite instead of Postgres.
- One container, one volume.
- Synchronous generation (no queue).
- TOML used only at seed time (no two-way sync).
- JSON columns for `defaults` and `app_source_cop` instead of normalised tables.
- Admin UI only edits structured data; AL file contents stay in the repo.
