# Unified extensions: the `[[extensions]]` data model

This document specifies the unified `[[extensions]]` data model that replaced the old Core-vs-modules asymmetry in the template schema. It covers the data model, TOML shape, generator changes, dependency resolution, the affix-substitution path, and the migration from the former `template_folders` / `template_module_folders` split.

**Status:** implemented. The model shipped in Phase 3 (entities `WorkspaceExtension{,Folder,File,Dependency}`, migration `20260514000000_UnifyExtensions`, `GenerationService`'s per-extension walk, and the `[[extensions]]` handling in `TemplateTomlMapper`). It supersedes the relevant sections of `domain-model.md`, `generation-engine.md`, and `templates-and-seeding.md`, which were rewritten to match. The old `template_folders` / `template_module_folders` tables no longer exist.

## Why

Today the model has two folder-scope tables. `template_folders` describes the Core extension's layout — Core is implicit, always emitted, one per template. `template_module_folders` describes scaffolding shared by **every** module selected for the workspace; module entities themselves carry id-range and dependencies but no folders/files of their own.

This breaks down the moment a template needs a second core-like extension (a customer-specific "Hotfix" extension always paired with Core) or a module-specific file (an `IFooContract.al` that only ships in the Foo module, not every module). Neither case has a clean representation: Hotfix-as-a-module forces its scaffolding into the shared module_folders, polluting other modules; module-specific files have no home at all.

The unified model fixes this by making "extension" the only unit. A template declares an ordered array of `[[extensions]]`; modules from the catalogue clone their own extension definition into the workspace at selection time.

## High-level shape

A workspace is N extensions:

- Template-declared extensions, marked `required = true` (always emitted) or `required = false` (opt-in on New Workspace).
- Module-cloned extensions: each module the user selects on New Workspace contributes one extension whose folders/files were defined on the module entity at catalogue-authoring time.

The generator walks the resulting extension list in declaration order, emits one extension folder per entry, and writes one entry per `[[extensions]]` into the `.code-workspace` `folders` array.

## TOML schema

```toml
[template]
key = "runtime-16"
runtime = "16.0"
name = "Runtime 16+"
description = "BC SaaS, namespace folders, AppSource-ready"
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200
is_default = false

[[template.default_modules]]   # default-selected catalogue modules
key = "continia-banking"

[defaults]
publisher = ""
target = "Cloud"
application = "27.0.0.0"        # form pre-fill; user can override on New Workspace
platform = "1.0.0.0"            # form pre-fill; user can override on New Workspace
extension_prefix = "ACME"       # form pre-fill; user can override on New Workspace
features = ["TranslationFile", "NoImplicitWith"]
supportedLocales = ["en-US", "da-DK"]
affix = "ACME"
affixType = "Prefix"            # "None" | "Prefix" | "Suffix"

[defaults.resourceExposurePolicy]
allowDebugging = true
allowDownloadingSource = false
includeSourceInSymbolFile = true

[workspace]
content = """
{
  "folders": [
{{paths}}
  ],
  "settings": { ... }
}
"""

# Always-included Core extension
[[extensions]]
path = "Core"
name = "{{extension_prefix}} Core"
required = true
# id_range_from / id_range_to optional — see "ID range allocation" below.

# Folders nest: each [[extensions.folders]] is a top-level folder under the
# extension; [[extensions.folders.folders]] are children, recursively. Files
# live as [[extensions.folders.files]] at any depth.
[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders.files]]
path = "AppInstall.Codeunit.al"
content = """
codeunit 90000 "{{affix}} App Install"
{
    Subtype = Install;
}
"""

# Optional Hotfix extension — admins flip required = false to show as a
# checkbox on New Workspace.
[[extensions]]
path = "Hotfix"
name = "{{extension_prefix}} Hotfix"
required = true
application = "27.0.0.0"      # per-extension override (optional)
runtime = "16.0"              # per-extension override (optional)

[[extensions.dependencies]]
extension = "Core"            # references another [[extensions]] entry by path

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders.files]]
path = "Hotfix.Codeunit.al"
content = """
codeunit 91000 "{{affix}} Hotfix"
{
}
"""
```

Notes:

- **`path` is the stable identifier** of an extension within a workspace. `name` is display-only (substituted), `path` is the immutable reference target for dependencies.
- **`required` defaults to true.** Template-declared extensions are always emitted unless explicitly marked optional. Optional extensions surface as checkboxes on New Workspace, alphabetised under "additional extensions."
- **Folders are a tree, not flat paths.** Each `path` is a single segment; nesting is expressed via `[[extensions.folders.folders]]` recursively. Files attach at any depth via `[[extensions.folders.files]]` / `[[extensions.folders.folders.files]]` / etc. Matches how admins think about the layout and lets us hang per-subtree metadata (e.g. a whole `Examples/` subtree marked `is_example`) without a flat-path workaround.
- **`application` and `runtime` are template-wide by default**, override-able per extension. Most workspaces use one BC version; the override unblocks the mixed-version edge cases.
- **`[[template.default_modules]]`** replaces the old top-level `default_modules` array. It lives under `[template]` rather than `[defaults]` because the list of default-selected modules is a workspace-composition concern, not app.json content. Each entry references a catalogue module by key.
- **No `[[module_folders]]`.** Module-supplied extensions carry their folders/files on the module entity itself (see "Modules as extension-template" below).

## `[template]` vs `[defaults]`

The split is:

- **`[template]`** carries authoring metadata (`key`, `name`, `description`, `is_default`, `runtime`), id-range policy (the four range fields), and the default-modules list. The generator reads the id ranges and the runtime; the UI reads everything else.
- **`[defaults]`** is the default starting state for the workspace plan — everything that gets pre-filled on the New Workspace form and ends up merged into every extension's `app.json`. Three categories of fields:
  - **Verbatim merge into every app.json:** `publisher`, `target`, `features`, `supportedLocales`, `affix`, `affixType`, `resourceExposurePolicy`.
  - **Form pre-fill, then merged in:** `application`, `platform`, `extension_prefix`.
  - *(none yet, but extensible)* per-extension override candidates.

The form populates from `[defaults]`, the user edits, the final values flow into the per-extension `app.json`. A second customer using the same template starts from the same defaults but overrides `extension_prefix` (and possibly `application` / `platform`) to suit their build.

## Workspace-time inputs

The `ProjectPlan` value object:

```csharp
record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,
    string ExtensionPrefix,        // NEW — pre-filled from defaults.extension_prefix
    string Brief,
    string Description,
    string ApplicationVersion,     // pre-filled from defaults.application
    string PlatformVersion,        // pre-filled from defaults.platform
    string RuntimeVersion,         // pre-filled from template.runtime
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    // bool IncludeForNav         REMOVED — modules-as-extension-template handles it
    IReadOnlyList<string> SelectedExtensionPaths,  // optional [[extensions]] entries the user ticked
    IReadOnlyList<string> SelectedModuleKeys
);
```

`ExtensionPrefix` is the per-workspace short identifier that surfaces as `{{extension_prefix}}` (e.g. "CRO" → "CRO Core"). It is distinct from `defaults.affix` — the affix is the object-name prefix/suffix (`"{{affix}} Setup"` → `"CONIT Setup"`), the extension prefix is the friendly extension-name prefix. Both can coexist on the same workspace.

`IncludeForNav` is gone — ForNAV becomes a normal catalogue module that templates list under `[[template.default_modules]]` when relevant.

## Modules as extension-template

A `Module` row in the catalogue keeps its identity (`key`, `name`, `id_range_size`, `dependencies`) and gains its own folders/files:

| New table                  | Replaces                              |
|----------------------------|---------------------------------------|
| `module_extension_folders` | `template_module_folders`             |
| `module_extension_files`   | `template_module_files`               |

When the user picks a module on New Workspace, the module's folder/file tree clones into the workspace's per-extension structure at generation time. The clone is in-memory only — modules stay normalised in the catalogue, the workspace never materialises a copy.

Each clone yields one entry in the workspace's extension list, with:

- `path = module.key` (folder name in the ZIP),
- `name = "{{extension_prefix}} {module.name}"` (default; templates can override the rendered name via a `module_name_template` field on `[[template.default_modules]]` if needed — punt for v1),
- folders/files from `module_extension_folders`/`module_extension_files`,
- dependencies from `module_dependencies` (existing table) plus an implicit dependency on every required template-declared extension (typically just Core).

The catalogue's "import from system org" flow already clones modules; it grows to also clone the new folder/file rows.

## ID range allocation

Each extension gets one `idRanges` entry in its `app.json`. Three layers:

1. **Explicit on the extension.** If `[[extensions]] id_range_from` and `id_range_to` are both set, use them verbatim.
2. **Module-supplied.** If the extension is a module clone and the module declares `id_range_size`, allocate from the workspace plan's `module_id_range_cursor` (see below) using the module's size.
3. **Auto-allocated from the template.** First template-declared extension consumes `core_id_range_from..core_id_range_to`. Subsequent unannotated template-declared extensions get `module_id_range_start + (index * module_id_range_size)`.

`module_id_range_cursor` starts at `template.module_id_range_start` and advances as modules consume slices in declaration order on New Workspace. This keeps backwards-compatibility with today's allocation behaviour — a template that doesn't think about id ranges produces the same numbers it does today.

Validation rule: id ranges within a single workspace must not overlap. The plan validator computes the resolved ranges and rejects the plan if any two overlap.

## Dependency resolution

`[[extensions.dependencies]]` is a uniform array. Each entry sets one of three reference fields:

```toml
# Intra-workspace: another [[extensions]] by path
[[extensions.dependencies]]
extension = "Core"

# Catalogue: a module catalogue row by key
[[extensions.dependencies]]
module = "system-application"

# Literal: anything not in any catalogue
[[extensions.dependencies]]
id = "63ca2fa4-4f03-4f2b-a480-172fef340d3f"
name = "System Application"
publisher = "Microsoft"
version = "27.0.0.0"
```

At generation, the dependency-resolver dispatches on which field is set:

- `extension = "X"` — find the workspace's extension with `path == "X"`, copy its freshly-generated `id`, its substituted `name`, the workspace publisher.
- `module = "K"` — find the module catalogue row, copy `dep_id` / `dep_name` / `dep_publisher` / `dep_version`. (Or, when modules-as-extension-template is selected on this workspace, point at the cloned extension's freshly-generated `id` — same lookup as `extension`.)
- Literal — emit the fields as-is.

References are by stable identifier (`path` or `key`), never display name, because display names get mustache-substituted and drift.

A dependency on an extension that doesn't exist in the workspace (optional but not selected, or a typo) fails validation at template-save time, not generation time — the editor refuses to save a template whose declared dependencies don't resolve.

## Affix and mustache substitution

Affix stays in `defaults.affix` / `defaults.affix_type`. The enum grows a third value:

```
AffixType = None | Prefix | Suffix
```

Mustache substitution simplifies. Three placeholders today (`{{prefix}}`, `{{suffix}}`, `{{affix}}`) collapse to one:

- `{{affix}}` — substitutes to the affix string, or empty when `AffixType == None`. Position is implicit in how the template author wrote the surrounding text (`"{{affix}} Setup"` vs `"Setup {{affix}}"`); templates are stylistically committed to one position so the conditional emission was overengineering.
- `{{prefix}}` and `{{suffix}}` are dropped from the substitution table. A migration step in the implementing PR rewrites existing template content to use `{{affix}}` in their place.

The generator surfaces the affix into per-extension AppSourceCop.json files when the template declares one as a `[[folders.files]]` entry — the file's content typically reads `"mandatoryAffix": "{{affix}}"` and substitution fills it in. Templates that don't ship an AppSourceCop.json (PTE-style) just don't get the field, which is the correct behaviour.

## Schema changes

Tables added:

- **`workspace_extensions`** keyed by `(template_id, path)`. Columns: `id`, `template_id` FK, `ordering`, `path`, `name_template`, `required`, `application` nullable, `runtime` nullable, `id_range_from` nullable, `id_range_to` nullable.
- **`workspace_extension_folders`** — recursive folder tree. Columns: `id`, `workspace_extension_id` FK (non-null on every row; denormalised so leaf queries don't walk the chain), `parent_folder_id` FK self-reference nullable (null on top-level folders), `ordering`, `path` (single segment, no `/`). Indexes: `(workspace_extension_id, parent_folder_id, ordering)` for ordered enumeration; `(parent_folder_id, path)` UNIQUE and `(workspace_extension_id, path) WHERE parent_folder_id IS NULL` UNIQUE so siblings can't collide.
- **`workspace_extension_files`** keyed by `(workspace_extension_folder_id, path)`. Columns: `id`, `workspace_extension_folder_id` FK, `ordering`, `path`, `content`, `is_example`. Files attach to any folder in the tree, including intermediate ones.
- **`workspace_extension_dependencies`** keyed by `(workspace_extension_id, ordering)`. Columns: `id`, `workspace_extension_id` FK, `ordering`, `ref_extension_path` nullable, `ref_module_key` nullable, `lit_id` nullable, `lit_name` nullable, `lit_publisher` nullable, `lit_version` nullable. CHECK constraint: exactly one of `ref_extension_path`, `ref_module_key`, `lit_id` is non-null.
- **`module_extension_folders`**, **`module_extension_files`** — mirror the workspace tables (recursive tree, files attach anywhere) but keyed on `module_id` instead of `workspace_extension_id`.

Tables removed:

- `template_folders`
- `template_files`
- `template_module_folders`
- `template_module_files`

Columns moved off `runtime_templates`:

- `default_application` → `defaults_json.application`
- `default_platform` → `defaults_json.platform`

Columns staying on `runtime_templates`:

- `workspace_template`, `runtime`, `core_id_range_from`, `core_id_range_to`, `module_id_range_start`, `module_id_range_size`, `is_default`, `is_system`, soft-delete + audit timestamps.

(The naming "workspace_extensions" is intentional — these are per-template definitions of extensions that go INTO a workspace. "extension_X" would collide with future extensibility-of-the-tool concepts.)

## Migration plan

One EF migration, `UnifyExtensions`. Its `Up`:

1. Create the new tables.
2. For each existing template:
   - Create a `workspace_extensions` row with `path = "Core"`, `name_template = "{{extension_prefix}} Core"`, `required = true`, `ordering = 0`, id-range columns null (auto-allocate from `core_id_range_from/to`).
   - Move `default_application` and `default_platform` into the `defaults_json` blob as the new `application` / `platform` fields. Drop the columns.
3. For each `template_folders` row, split `path` on `/` and walk/create the tree in `workspace_extension_folders` under the Core extension. Single-segment paths land as top-level rows (parent null); slash-separated paths get intermediate rows created on first sight. Preserve relative ordering on top-level siblings.
4. Copy every `template_files` row into `workspace_extension_files` attached to the *leaf* folder produced in step 3, preserving file ordering, paths (the basename), content, is_example.
5. For each module, run steps 3–4 against `template_module_folders` / `template_module_files` into `module_extension_folders` / `module_extension_files` *for that module*. This is the lossy step: today's `template_module_folders` is shared across all modules from a template, so the migration duplicates it onto every module that's in that template's `default_modules` list. Modules that aren't in any template's default list get an empty folder set — admins fix them up afterwards via the admin UI.
6. Rewrite mustache `{{prefix}}` / `{{suffix}}` to `{{affix}}` in every `content` column.
7. Drop the old tables.

The `Down` migration is one-way: restoring the symmetry would require collapsing per-module folders back into a template-wide shared set, *and* re-flattening the tree into slash-separated paths — both information-destroying. We accept that this migration is forward-only.

## Open questions

The doc deliberately punts on:

- **Per-module name templates.** If an admin wants to override the rendered name of a module-supplied extension (e.g. force "Continia Banking" → "{{extension_prefix}} Banking"), the cleanest place is a `module_name_template` field on `[[template.default_modules]]`. Punt to a follow-up — the implicit `"{{extension_prefix}} {module.name}"` covers the common case.
- **External-file references for content.** TOML files will get long. We considered `content_ref = "path/to/file.al"` to externalise AL content but rejected it for v1 — splits the source of truth and breaks copy-paste portability.
- **Validation of cyclic dependencies.** Template-save validation should reject a template where extension A depends on extension B which depends on A. Straightforward to implement; called out here so it doesn't fall off the punch list.

## Impact on other docs

This work landed and the affected docs were rewritten to match:

- `domain-model.md` — the templates section now describes the new tables; the four removed ones are gone.
- `generation-engine.md` — the algorithm section is built around the unified extension walk; "Core-vs-modules" language was dropped.
- `templates-and-seeding.md` — the TOML schema section uses `[[extensions]]`; the old `[[folders]]` / `[[module_folders]]` examples were removed.
- `completed-milestones.md` — the migration is recorded as a one-way step so anyone reading later understands why a `Down` doesn't restore the prior data shape.

`CLAUDE.md` doesn't need changes — the architectural fences (PG as source of truth, multi-tenant, synchronous generation, no client framework) all still hold.
