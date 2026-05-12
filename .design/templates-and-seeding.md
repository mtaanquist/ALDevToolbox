# Templates and seeding

## Where templates live

**Persistence:** in the PostgreSQL database. Templates, modules, and catalogue entries are stored as rows in the tables described in `domain-model.md`. The DB is the source of truth at runtime — every read on the user-facing flows hits these tables.

**Authoring surfaces:** two equal options, both writing through the same `TemplateInput` validation pipeline into the DB.

1. **The structured admin form.** Field-by-field editing on `/admin/templates/{key}`. Best for incremental tweaks, single-flag changes, and folder reordering.
2. **TOML.** A textarea on the same admin page (toggle in the header) that round-trips a `template.toml` document. Best for authoring a new template from scratch, bulk folder edits, and copy-pasting a template from one environment to another. New templates default to TOML mode because that's the workflow this is optimised for.

**Bootstrap:** the singleton **system organisation** is the canonical source other orgs fork from. The Default org carries `organizations.is_system = true` (stamped by migration `20260513000000_MoveSeedToSystemOrg`); a partial unique index refuses a second system org per deployment. SiteAdmins author templates, modules and application versions there via the regular `/admin/templates*` pages — the system org is otherwise a normal org from the data model's point of view.

Every other organisation starts **empty**. New-org signup (auto-approved or admin-approved) no longer triggers any seed step; the admin lands on `/admin/templates` with two ways to populate it:

1. **Import from site.** `/admin/templates` renders a "From the site catalogue" section listing the system org's published templates. Clicking **Import** copies the chosen template (plus its referenced modules and default application version) into the local org via `TemplateImportService.ImportTemplateAsync`. Imports are one-way clones — once a template is in the local org, system-side edits no longer propagate.
2. **Author from scratch.** The structured form and TOML editor work exactly as before; the org simply doesn't have any starting content until the admin adds some.

The retired on-disk `Templates.seed/` directory is gone, along with `SeedService`. The `OrganizationConfigService.PopulateDefaultsAsync` pipeline and the disk-default logo are also gone — new orgs start with no logo and no settings row; admins fill them in via `/admin/configuration`.

This split exists so:
- The deployment has exactly one canonical catalogue that SiteAdmins curate at runtime, instead of a per-PR file-system bootstrap.
- New orgs come up empty and pull only the templates they care about, rather than the entire historical seed snapshot.
- Day-to-day edits — and authoring entire new templates — don't require a PR + redeploy.
- Admins comfortable with TOML can stay in TOML; admins who prefer forms can stay in forms.

## Import strategy

`TemplateImportService.ImportTemplateAsync(systemTemplateId)` clones one template into the acting organisation. Pseudocode:

```
TemplateImportService.ImportTemplateAsync(systemTemplateId):
    require an authenticated org context
    refuse if the acting org IS the system org (would import from itself)
    resolve the system org via IsSystem = true (bypassing query filters)
    load the source template with all children (folders, module folders,
        default modules + their dependencies, default application version)
    refuse if the local org already has a template with the same key
    for each referenced Module:
        reuse the local module that shares the key, otherwise clone it
        (with its module_dependencies) into the local org
    for the default application version:
        reuse the local row that shares the key, otherwise clone it
    insert a fresh runtime_template row (organization_id = acting org)
        plus cloned folder + file rows
    return the new template
```

The full graph commits in a single `SaveChangesAsync`. The audit interceptor records every insert, so the importing org's audit log carries the operation in full.

`ListSystemTemplatesAsync()` is the read-side counterpart — it returns the system org's active templates with an `AlreadyImported` flag so the UI can render "Already imported" instead of an Import button on rows the local org already has.

Idempotency is per-template-key per-org: a second click on the same row throws `PlanValidationException("Key", …)` rather than silently overwriting.

## Tomlyn usage

Tomlyn deserialises directly into POCOs. `Domain/Seed/` carries the mirrored types used by the admin TOML editor and the export pipeline (it is no longer wired to any on-disk bootstrap):

```csharp
class TemplateSeed {
    public TemplateMetaSeed Template { get; set; }
    public DefaultsSeed Defaults { get; set; }      // includes the template-wide affix + affixType block
    public WorkspaceSeed Workspace { get; set; }  // verbatim .code-workspace content with {{paths}} placeholder
    public List<FolderSeed> Folders { get; set; }
    public List<FolderSeed> ModuleFolders { get; set; }
}
class WorkspaceSeed {
    public string Content { get; set; }            // emitted as a TOML multi-line basic string in the [workspace] table
}
class FolderSeed {
    public string Path { get; set; }       // empty string == extension root (files land next to app.json)
    public List<FolderFileSeed> Files { get; set; } = new();
}
class FolderFileSeed {
    public string Path { get; set; }       // relative to the folder, e.g. "AppInstall.Codeunit.al"
    public string Content { get; set; }    // raw file content; mustache substitution runs at generation time for .al files
    public bool IsExample { get; set; }    // skipped when the workspace "Include example AL files" toggle is off; omitted from TOML when false
}
// ...etc
```

`Toml.ToModel<TemplateSeed>(text)` does the deserialisation. The `defaults_json` column is populated by re-serialising the `Defaults` sub-object to JSON. There is no longer an `app_source_cop_json` column — AppSourceCop.json is just a regular file under a root-path folder.

## Template TOML schema

A `template.toml` document — pasted into the admin TOML editor or produced by the export pipeline — follows this shape:

```toml
# Required: template metadata
[template]
key = "runtime-15"               # URL-safe unique key
runtime = 15                     # the AL runtime version
name = "Runtime 15+"             # display name
description = "Namespace folders" # caption under the dropdown
default_application = "24.0.0.0" # used to pre-fill the form
default_platform = "1.0.0.0"
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200
# Optional: module keys pre-selected on the New Workspace form when this
# template is picked. End-users can still opt out of any entry; this only
# seeds the initial selection. Unknown keys are dropped at seed time with a
# warning rather than failing the import.
default_modules = ["foundation", "document-capture"]

# Required: defaults merged into every generated app.json
[defaults]
publisher = "Consortio IT"
target = "Cloud"
url = "https://www.consortio.dk/"
logo = "../.assets/images/logo.png"
features = ["TranslationFile", "NoImplicitWith"]
supportedLocales = ["en-US", "da-DK"]
affix = "CONIT"                  # drives {{prefix}}, {{suffix}}, {{affix}} mustache vars
affixType = "Prefix"             # "Prefix" or "Suffix" — decides which of {{prefix}}/{{suffix}} emits

[defaults.resourceExposurePolicy]
allowDebugging = true
allowDownloadingSource = false
includeSourceInSymbolFile = true

# Required: verbatim .code-workspace content. The generator substitutes
# {{paths}} with the workspace's folder entries (Core + every selected
# module). Everything else — settings, analyzers, ruleset path,
# recommended extensions — is yours to customise per template.
[workspace]
content = """
{
  "folders": [
{{paths}}
  ],
  "settings": {
    "editor.formatOnSave": true,
    "al.codeAnalyzers": ["${CodeCop}", "${AppSourceCop}", "${UICop}"],
    "al.enableCodeAnalysis": true,
    "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
  },
  "extensions": {
    "recommendations": ["ms-dynamics-smb.al"]
  }
}
"""

# Required: array of folder definitions, in display order. These are emitted
# into the Core extension only (and into the single extension produced by the
# standalone New Extension flow). Module extensions in a generated workspace
# use [[module_folders]] below — see generation-engine.md for why.
#
# An empty path means the folder represents the **extension root** — its
# files land directly next to app.json. This is how AppSourceCop.json is
# now expressed (it used to be a hard-coded generator output sourced from
# the retired app_source_cop_json column). At most one empty-path folder
# per [[folders]] / [[module_folders]] list.
[[folders]]
path = ""

[[folders.files]]
path = "AppSourceCop.json"
content = """
{
    "mandatoryPrefix": "CONIT",
    "supportedCountries": ["US", "DK"]
}
"""

[[folders]]
path = "Source/Foundation"

# Optional: file contents seeded into this folder. When empty, the folder
# generates with a single .gitkeep regardless of the include-examples toggle.
# Mustache substitution runs at generation time for .al files; non-AL files
# are written verbatim. `is_example = true` marks the file as skippable when
# the end user clears the workspace's "Include example AL files" checkbox;
# the flag is omitted from TOML when false to keep diffs quiet.
[[folders.files]]
path = "AppInstall.Codeunit.al"
is_example = true
content = """
namespace {{namespace}};

codeunit 90100 "{{prefix}} App Install"
{
    Subtype = Install;
}
"""

[[folders]]
path = "Source/Sales"
# no files — gets .gitkeep regardless of the include-examples toggle

[[folders]]
path = "Translations"

# Optional: array of folders emitted into every module extension. Empty (or
# omitted) means modules ship with just app.json and the static fallback
# folders (libs/, permissionsets/, Translations/). Same shape as [[folders]],
# including the empty-path "extension root" row for an AppSourceCop.json or
# other module-root file.
[[module_folders]]
path = "Source"
```

## Module TOML schema

Each module's TOML document:

```toml
[module]
key = "document-capture"
name = "Document Capture"
id_range_size = 200             # optional; null/missing = use template default

[[module.dependencies]]
dep_id = "4b915d7e-c02a-435f-85ab-649086c1e002"
dep_name = "Continia Core"
dep_publisher = "Continia Software"
dep_version = "1.0.0.0"

[[module.dependencies]]
dep_id = "0745e76d-0b72-4641-87c2-ee45db5d2c32"
dep_name = "Continia Delivery Network"
dep_publisher = "Continia Software"
dep_version = "1.0.0.0"
```

Order of `[[module.dependencies]]` blocks is preserved.

## Catalogue TOML schema

The catalogue TOML document carries every well-known dep in one file:

```toml
[[dependency]]
dep_id = "4b915d7e-c02a-435f-85ab-649086c1e002"
dep_name = "Continia Core"
dep_publisher = "Continia Software"
dep_version_default = "1.0.0.0"
category = "Continia"

[[dependency]]
dep_id = "6f0293d3-86fc-4ff8-9632-54a580be6546"
dep_name = "ForNAV Core"
dep_publisher = "ForNAV"
dep_version_default = "1.0.0.0"
category = "ForNAV"
```

## Mustache variables (reference)

These are the variables available when seeding example AL files into a generated extension. See `generation-engine.md` for substitution behaviour.

| Variable             | Description                                   |
|----------------------|-----------------------------------------------|
| `{{name}}`           | Full extension name                           |
| `{{workspaceName}}`  | The workspace name from the form              |
| `{{shortName}}`      | Workspace name with whitespace removed        |
| `{{moduleName}}`     | The module's display name                     |
| `{{publisher}}`      | Publisher field from defaults                 |
| `{{prefix}}`         | `affix` from defaults when `affixType = "Prefix"`; empty otherwise |
| `{{suffix}}`         | `affix` from defaults when `affixType = "Suffix"`; empty otherwise |
| `{{affix}}`          | `affix` from defaults, always                 |
| `{{namespace}}`      | The folder path, dot-separated                |
| `{{guid}}`           | A fresh GUID per call                         |
| `{{paths}}`          | `.code-workspace` content only: workspace's folder entries |

## TOML as an authoring surface

Both `/admin/templates/new` and `/admin/templates/{key}` expose a TOML editor next to the structured form via a header toggle. The new-template flow opens in TOML mode by default because pasting in a `template.toml` is usually the fastest way to get a fresh template going; the edit flow opens in the structured form because surgical tweaks are what people do most often there.

Saving from TOML mode parses the textarea via Tomlyn, maps it onto the same `TemplateInput` payload the structured form produces, and runs through identical validation. There is no separate code path on the way to the database.

The mapper (`Services/TemplateTomlMapper.cs`) is load-bearing infrastructure for the admin editor and the export pipeline. Two things to keep in mind when touching it:

- One TOML schema covers the admin editor, the export ZIP, and any future import-from-TOML affordance. Don't fork it.
- A few fields are deliberately not represented in the TOML view: `deprecated` (flip it from the structured form's checkbox), and per-row folder reordering by drag (express the order by writing the `[[folders]]` blocks in that order; array order is preserved as `ordering`).
- `[[folders.files]]` blocks make the TOML lossless even when folders carry file content. For larger files this gets noisy; the structured editor is usually the more comfortable surface for editing AL source. Both write through the same pipeline, so neither is "more correct" than the other.

## Export to TOML

`/admin` exposes a one-click "Export all to TOML" button that produces a ZIP containing the current state of the database serialised into TOML. This is the safety hatch for:

- Periodic snapshotting into a backup branch.
- Quickly grepping/diffing the entire template state outside the app.
- Disaster recovery if the database is unrecoverable (paste the templates back via the admin TOML editor).

Implementation: walk all active rows, serialise each template/module/catalogue back into the TOML shape, ZIP into `aldt-export-<datestamp>.zip`. Use Tomlyn's `Toml.FromModel(...)` for the inverse direction.

## What is *not* admin-editable

- The static `.gitignore` template and the AL ruleset JSON. These ship as embedded resources under `Resources/` because they're per-deployment policy rather than per-template content. Treat them as code.
- The `README.md` boilerplate written into a generated workspace — it's emitted by `GenerationService` and shaped by template metadata, not edited directly.
- Binary files inside template folders. v1 stores file content as UTF-8 text only; PNGs, ZIPs, or anything else non-text don't have a place in `template_files`. If we need binary template assets later, the likely shape is a separate `template_file_blobs` table or a URL-fetched asset; defer until there's a real ask.

The org logo, default publisher / ID range / brief / core description, and the always-included files appended to every generated workspace **are** admin-editable from `/admin/configuration` and live in the database (`organization_assets`, `organization_settings`, `organization_files`). Fresh orgs start without any of these rows — admins fill them in once they need them.
