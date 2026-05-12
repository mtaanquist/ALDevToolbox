# Domain model

This document specifies the entities, their relationships, and the PostgreSQL schema. EF Core code-first migrations are the expected mechanism for creating the schema; the SQL types below are the target shape.

The schema was a SQLite one through v1 (M1–M15) and switched to PostgreSQL 18 in P4.16. The two changes that matter at this layer are:

- `defaults_json` and `app_source_cop_json` are now `jsonb` columns. The C# value-converter still serialises through a `string` round-trip, so application code is unchanged. No JSONB GIN index yet — add one when a query needs it.
- All `DateTime` columns are `timestamp with time zone`. Application code is already disciplined about `DateTime.UtcNow` / `DateTimeKind.Utc` literals, and `OnModelCreating` pins the column type so a new column added without an explicit type still lands correctly.

## Entities

```
RuntimeTemplate ──┐
                  ├── has many ──> TemplateFolder (ordered, Core only)
                  │                    └── has many ──> TemplateFile (ordered)
                  ├── has many ──> TemplateModuleFolder (ordered, modules only)
                  │                    └── has many ──> TemplateModuleFile (ordered)
                  ├── has many ──> RuntimeTemplateDefaultModule (ordered) ──> Module
                  └── has one ────> TemplateDefaults (JSON column on the row)

Module ───────────┐
                  ├── has many ──> ModuleDependency (ordered)
                  └── id-range fields on the row

WellKnownDependency  (flat list, used by New Extension flow's dep picker)

AuditLogEntry        (one row per change to any of the above)

AuthSession          (optional — see auth-and-audit.md for cookie strategy)
```

## Tables

### `runtime_templates`

The primary template entity — corresponds one-to-one with a runtime version (or rather, a *named* template; you might have multiple per runtime in theory, though we don't expect to).

| Column                  | Type           | Notes                                              |
|-------------------------|----------------|----------------------------------------------------|
| `id`                    | INTEGER PK     | Autoincrement                                      |
| `key`                   | TEXT NOT NULL  | Unique. Used in URLs and the dropdown. e.g. "runtime-15" |
| `runtime`               | INTEGER NOT NULL | The AL runtime version, e.g. 15                  |
| `name`                  | TEXT NOT NULL  | Display name in the dropdown                       |
| `description`           | TEXT           | Caption under the dropdown selection               |
| `default_application`   | TEXT NOT NULL  | e.g. "24.0.0.0" — pre-fills the form               |
| `default_platform`      | TEXT NOT NULL  | e.g. "1.0.0.0"                                     |
| `defaults_json`         | TEXT NOT NULL  | JSON blob of remaining `app.json` defaults (target, features, supportedLocales, url, logo, resourceExposurePolicy) plus the template-wide affix block (`affix` string, `affixType` "Prefix" \| "Suffix") that drives the `{{prefix}}` / `{{suffix}}` / `{{affix}}` mustache vars. The `app_source_cop_json` column was retired — templates that need an AppSourceCop.json declare it as a regular `template_files` row under a folder with empty `path`. |
| `core_id_range_from`    | INTEGER NOT NULL | Default 90000                                    |
| `core_id_range_to`      | INTEGER NOT NULL | Default 90999                                    |
| `module_id_range_start` | INTEGER NOT NULL | Default 91000 — first module gets this           |
| `module_id_range_size`  | INTEGER NOT NULL | Default 200 — span per module                    |
| `deprecated`            | INTEGER NOT NULL | 0/1 boolean. Hidden from the dropdown if 1       |
| `created_at`            | TEXT NOT NULL  | UTC ISO8601                                        |
| `updated_at`            | TEXT NOT NULL  | UTC ISO8601                                        |
| `deleted_at`            | TEXT           | Null = active. Soft-delete                         |

`defaults_json` is a JSON column rather than a normalised table because it's write-once-edit-rarely metadata, and the structure is closed (defined by the AL/BC ecosystem). Normalising it would mean a schema migration every time AL adds a new app.json field. EF Core uses `HasConversion<string>()` with `JsonSerializer` to make this typed in C# while stored as text.

### `template_folders`

The folder structure the runtime template emits into the **Core** extension only — and into the single extension folder produced by the standalone (New Extension) flow. Each row is one folder.

| Column         | Type           | Notes                                          |
|----------------|----------------|------------------------------------------------|
| `id`           | INTEGER PK     |                                                |
| `template_id`  | INTEGER FK     | → runtime_templates.id, cascade delete          |
| `ordering`     | INTEGER NOT NULL | Position within the template's folder list   |
| `path`         | TEXT NOT NULL  | Relative path inside the extension folder. e.g. "Source/Foundation". An **empty** path means the folder represents the extension root — its files land next to app.json. At most one empty-path row per template (enforced by validation). |

Index: `(template_id, ordering)`.

A folder's seeded contents live in `template_files` rows hung off this table. A folder with no `template_files` rows generates a single `.gitkeep` placeholder; a folder with rows generates one entry in the ZIP per row whose `is_example` is false (or true when the workspace's "Include example AL files" toggle is on). When all files filter out, the generator falls back to `.gitkeep` — except for the extension-root folder, which never needs one (app.json is always there).

Module extensions in a generated workspace use `template_module_folders` instead — see below — so Core's per-extension scaffolding (App Install codeunits, setup tables, permission sets) doesn't get duplicated into every module ZIP.

### `template_module_folders`

Same shape as `template_folders`, but rows are emitted into every **module** extension generated from this template. Empty by default; if a template has no module folders, modules ship with just `app.json` and the static fallback folders (`libs/`, `permissionsets/`, `Translations/`). Like `template_folders`, an empty-path row represents the module extension's root — admins typically use it to seed an AppSourceCop.json for AppSource-published modules.

| Column         | Type           | Notes                                          |
|----------------|----------------|------------------------------------------------|
| `id`           | INTEGER PK     |                                                |
| `template_id`  | INTEGER FK     | → runtime_templates.id, cascade delete          |
| `ordering`     | INTEGER NOT NULL | Position within the template's module-folder list |
| `path`         | TEXT NOT NULL  | Relative path inside the generated module extension |

Index: `(template_id, ordering)`.

Per-folder file content lives in `template_module_files` (next).

### `template_module_files`

Per-module-folder file content. Same shape as `template_files`, just hung off `template_module_folders`.

| Column                      | Type           | Notes                                          |
|-----------------------------|----------------|------------------------------------------------|
| `id`                        | INTEGER PK     |                                                |
| `template_module_folder_id` | INTEGER FK     | → template_module_folders.id, cascade delete    |
| `ordering`                  | INTEGER NOT NULL | Position within the folder's file list        |
| `path`                      | TEXT NOT NULL  | Relative path inside the folder, forward-slash separated. Same rules as `template_files.path` |
| `content`                   | TEXT NOT NULL  | Raw file content. Mustache substitution runs at generation time for `.al` files |
| `is_example`                | BOOLEAN NOT NULL | Skipped at generation time when the workspace "Include example AL files" toggle is off. Default false; existing rows backfilled to true by migration |

Indexes: `(template_module_folder_id, ordering)`, `(template_module_folder_id, path)` UNIQUE.

### `template_files`

Per-folder file content. Stored as UTF-8 text — binary assets are not supported in v1.

| Column              | Type           | Notes                                          |
|---------------------|----------------|------------------------------------------------|
| `id`                | INTEGER PK     |                                                |
| `template_folder_id`| INTEGER FK     | → template_folders.id, cascade delete           |
| `ordering`          | INTEGER NOT NULL | Position within the folder's file list        |
| `path`              | TEXT NOT NULL  | Relative path inside the folder, forward-slash separated. e.g. "AppInstall.Codeunit.al" or "subfolder/Util.Codeunit.al". No leading slash, no `..` segments. |
| `content`           | TEXT NOT NULL  | Raw file content. Mustache substitution runs at generation time for `.al` files. |
| `is_example`        | BOOLEAN NOT NULL | Skipped at generation time when the workspace "Include example AL files" toggle is off. Default false; existing rows backfilled to true by migration so the pre-flag all-or-nothing behaviour is preserved. |

Indexes: `(template_folder_id, ordering)` for ordered enumeration; `(template_folder_id, path)` UNIQUE so a single folder can't carry two files at the same relative path.

Editing `path` or `content` flows through the same `TemplateInput` pipeline both authoring surfaces use. The structured editor offers per-file path inputs and a `<textarea>` for content; the TOML editor expresses the same data via `[[folders.files]]` blocks (see `templates-and-seeding.md`).

### `runtime_template_default_modules`

Pre-selected modules per template (Milestone P2.1). When a user picks a template on the New Workspace form, the modules listed here are ticked automatically — they have to opt out rather than in.

| Column                | Type             | Notes                                         |
|-----------------------|------------------|-----------------------------------------------|
| `id`                  | INTEGER PK       |                                               |
| `runtime_template_id` | INTEGER FK       | → runtime_templates.id, cascade delete         |
| `module_id`           | INTEGER FK       | → modules.id, cascade delete                   |
| `ordering`            | INTEGER NOT NULL | Display order in the admin's chosen sequence  |

Indexes: `(runtime_template_id, ordering)` for ordered enumeration; `(runtime_template_id, module_id)` UNIQUE so the same module can't appear twice on a template's default list.

The relation is purely cosmetic for end-users — generation does not consult it. A user who deselects a default module gets a workspace without that module, just as they would by leaving an unticked module unticked. Soft-deleted modules are silently dropped from the pre-selection at form load time.

### `modules`

| Column                   | Type           | Notes                                          |
|--------------------------|----------------|------------------------------------------------|
| `id`                     | INTEGER PK     |                                                |
| `key`                    | TEXT NOT NULL  | Unique. URL-safe, e.g. "document-capture"      |
| `name`                   | TEXT NOT NULL  | Display name e.g. "Document Capture"           |
| `id_range_size`          | INTEGER        | Optional override; null = use the template's `module_id_range_size` |
| `deprecated`             | INTEGER NOT NULL | 0/1                                          |
| `created_at`             | TEXT NOT NULL  |                                                |
| `updated_at`             | TEXT NOT NULL  |                                                |
| `deleted_at`             | TEXT           |                                                |

Modules are not tied to a specific runtime — they declare dependencies that work across runtimes. The runtime template owns the folder layout; the module just contributes to `app.json`'s `dependencies` array.

### `module_dependencies`

| Column         | Type           | Notes                                                |
|----------------|----------------|------------------------------------------------------|
| `id`           | INTEGER PK     |                                                      |
| `module_id`    | INTEGER FK     | → modules.id, cascade delete                          |
| `ordering`     | INTEGER NOT NULL |                                                    |
| `dep_id`       | TEXT NOT NULL  | The GUID of the dependency, e.g. "4b915d7e-..."      |
| `dep_name`     | TEXT NOT NULL  | e.g. "Continia Core"                                 |
| `dep_publisher`| TEXT NOT NULL  | e.g. "Continia Software"                             |
| `dep_version`  | TEXT NOT NULL  | e.g. "1.0.0.0"                                       |

Index: `(module_id, ordering)`.

The admin module editor sources dependencies from `well_known_dependencies`: picking a catalogue entry inserts a `module_dependencies` row with the GUID, name, publisher, and the catalogue's default version copied in. The columns are intentionally a snapshot — once added, editing or deleting the source catalogue entry does not change existing modules, so generation stays stable across catalogue maintenance. `dep_version` is the only field admins edit per-module (to pin a specific version that differs from the catalogue default). Rows whose GUID no longer matches any catalogue entry (legacy data, or entries deleted from the catalogue) are still rendered in the editor with a "Not in catalogue" hint and remain removable.

### `well_known_dependencies`

The catalogue of "things you might depend on without us knowing in advance." It's the single source admins pick from when composing modules (above) **and** when adding dependencies to a standalone extension from the New Extension page. The table is kept deliberately separate from `module_dependencies` because:

- Modules group dependencies into sets. Well-known deps are flat.
- The same dependency might appear in multiple modules' lists (Continia Core appears in three).
- Admins should be able to add new well-known deps without creating a fake module for them.

| Column         | Type           | Notes                                                |
|----------------|----------------|------------------------------------------------------|
| `id`           | INTEGER PK     |                                                      |
| `dep_id`       | TEXT NOT NULL  | GUID                                                 |
| `dep_name`     | TEXT NOT NULL  |                                                      |
| `dep_publisher`| TEXT NOT NULL  |                                                      |
| `dep_version_default` | TEXT NOT NULL  | Pre-fills the version field; the user can override it for their specific extension |
| `category`     | TEXT           | Optional grouping label e.g. "Continia", "ForNAV", "Other" — used to group items in the picker UI |
| `ordering`     | INTEGER NOT NULL |                                                    |
| `created_at`   | TEXT NOT NULL  |                                                      |
| `updated_at`   | TEXT NOT NULL  |                                                      |

### `audit_log`

One row per write to any of the above tables. See `auth-and-audit.md` for the full behaviour.

| Column         | Type           | Notes                                                |
|----------------|----------------|------------------------------------------------------|
| `id`           | INTEGER PK     |                                                      |
| `timestamp`    | TEXT NOT NULL  | UTC ISO8601                                          |
| `changed_by`   | TEXT NOT NULL  | The honour-system display name from login            |
| `entity_type`  | TEXT NOT NULL  | "runtime_template" \| "template_folder" \| "template_file" \| "template_module_folder" \| "template_module_file" \| "runtime_template_default_module" \| "module" \| "module_dependency" \| "well_known_dependency" |
| `entity_id`    | INTEGER NOT NULL | The id within that entity's table                  |
| `action`       | TEXT NOT NULL  | "created" \| "updated" \| "deleted"                  |
| `snapshot_json`| TEXT           | Full JSON of the row's state *before* the change. Null for "created" |

Index: `(entity_type, entity_id, timestamp DESC)` for the per-entity history view.

## Validation rules

These are domain rules — they should be enforced in service code, not just at the form layer. In particular, anything that calls `TemplateService.Update` should fail before hitting the DB if these are violated.

- `runtime_templates.key` must match `^[a-z0-9-]+$` and be unique.
- `runtime_templates.runtime` must be > 0.
- `runtime_templates.core_id_range_from < core_id_range_to`.
- `template_folders.path` must be a relative path with `/` separators, no leading slash, no `..`.
- `template_files.path` must be a relative path with `/` separators, no leading slash, no `..`. It is unique per `template_folder_id`.
- `template_files.content` is UTF-8 text. Audit snapshots store a SHA-256 hash of the content (plus the path) rather than the content itself, so the audit log doesn't bloat with copies of every AL file on every save.
- `modules.key` must match `^[a-z0-9-]+$` and be unique.
- `module_dependencies.dep_id` must be a GUID format.
- `well_known_dependencies.dep_id` must be a GUID format.

## Soft-delete and "deprecated" semantics

These are deliberately separate flags:

- **`deprecated`** — visible in admin lists, hidden from end-user-facing dropdowns. Used for "we don't recommend this anymore but old projects still need to be regeneratable."
- **`deleted_at`** — soft-deleted. Hidden from both admin and end-user lists by default; recoverable through a "show deleted" toggle in the admin section.

Hard delete is not exposed in the UI. Hard delete only happens via direct database access if someone really needs to clean up.
