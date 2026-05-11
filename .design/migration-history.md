# Migration history

One-line summary per EF Core migration in `ALDevToolbox/Data/Migrations/`. Read each migration's class XML doc for the gritty details — this list is for orientation only.

| Timestamp        | Name                              | Summary |
|------------------|-----------------------------------|---------|
| 20260508 105816  | `InitialCreate`                   | First schema: `runtime_templates`, `template_folders`, `modules`, `module_dependencies`, `well_known_dependencies`, `audit_log`. |
| 20260509 000000  | `AddTemplateFiles`                | Splits per-folder example AL out of disk and into the new `template_files` table. Drops the old `example_path` column on `template_folders` after backfilling. |
| 20260510 000000  | `AddAuditLogTimestampIndex`       | Standalone `(timestamp)` index on `audit_log` so the `/admin/audit` overview doesn't full-sort. |
| 20260511 000000  | `AddRuntimeTemplateDefaultModules`| `runtime_template_default_modules` join table — admin-curated modules that pre-tick on the New Workspace form (P2.1). |
| 20260511 000001  | `AddTemplateModuleFolders`        | Splits per-extension scaffolding into Core-only `template_folders` and module-only `template_module_folders` / `template_module_files`. Fixes module ZIPs duplicating Core's folders. |
| 20260512 000000  | `RuntimeColumnAsString`           | `runtime_templates.runtime` becomes TEXT so it can carry dotted versions (`"15.2"`) alongside bare majors. |
| 20260513 000000  | `AddApplicationVersions`          | Curated `application_versions` catalogue + `default_application_version_id` FK on `runtime_templates` (P2.4). Replaces free-text Application/Runtime fields with a select. |
| 20260514 000000  | `AddOrganizationsAndAccounts`     | Multi-tenancy + accounts (P3.13). Adds `organizations`, `users`, `signup_requests`, `password_reset_tokens`, `login_attempts`. Stamps every editable row with `organization_id` (Default org = 1). Replaces the shared admin password. |
| 20260515 000000  | `AddOrganizationConfiguration`    | Per-org config (P3.14). Adds `organization_settings`, `organization_assets` (logo BLOB), `organization_files` (always-included files). |
| 20260513 000000  | `MoveSeedToSystemOrg`             | Retires the on-disk `Templates.seed/` bootstrap. Renames `organizations.is_seeded` to `is_system`, stamps the Default org as the singleton system org other orgs fork from via `TemplateImportService`, and adds a partial unique index that refuses a second system org per deployment. |

## Conventions

- **One milestone per migration where possible.** A migration with two unrelated concerns is a code-review red flag.
- **Migration files are immutable once merged.** New schema changes go in a new migration, never as edits to a shipped one.
- **`MigrateAsync` runs at startup**; the app refuses to serve traffic if it can't apply pending migrations. There is no separate migrate-then-deploy step.
- **The model snapshot (`AppDbContextModelSnapshot.cs`) is partly hand-maintained** for the M14+ migrations because EF's reverse-engineering doesn't capture every detail of multi-tenant query filters. The pending-changes warning is suppressed in `Program.cs`; real schema drift still surfaces at `MigrateAsync` time. Run `dotnet ef migrations add <Name>` and review what EF generated before committing.
