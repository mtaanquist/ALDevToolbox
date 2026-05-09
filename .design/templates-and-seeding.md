# Templates and seeding

## Where templates live

**Persistence:** in the SQLite database. Templates, modules, and catalogue entries are stored as rows in the tables described in `domain-model.md`. The DB is the source of truth at runtime — every read on the user-facing flows hits these tables.

**Authoring surfaces:** two equal options, both writing through the same `TemplateInput` validation pipeline into the DB.

1. **The structured admin form.** Field-by-field editing on `/admin/templates/{key}`. Best for incremental tweaks, single-flag changes, and folder reordering.
2. **TOML.** A textarea on the same admin page (toggle in the header) that round-trips a `template.toml` document. Best for authoring a new template from scratch, bulk folder edits, and copy-pasting a template from one environment to another. New templates default to TOML mode because that's the workflow this is optimised for.

**Bootstrap:** `Templates.seed/` is the source-controlled starting point. On first run against an empty DB, every TOML file under it is imported, and the example AL files alongside each `template.toml` are slurped into `template_files` rows so admins can edit them without touching disk. After that, the seed files are ignored — neither a fallback, nor a sync source. Edits made through either authoring surface live in the DB only; nothing rewrites `Templates.seed/`.

This split exists so:
- The repo has a sensible "starting point" for fresh deployments that's source-controlled and reviewable.
- Day-to-day edits — and authoring entire new templates — don't require a PR + redeploy.
- Admins comfortable with TOML can stay in TOML; admins who prefer forms can stay in forms.
- Backups / snapshots can be exported back to TOML if anyone wants the source-control workflow back temporarily.

## Seed strategy

The `SeedService` runs once at app startup. Pseudocode:

```
on app start:
    if database has no runtime_templates rows:
        for each subfolder of Templates.seed/runtime-* :
            parse template.toml
            insert runtime_template row
            for each [[folders]] entry:
                insert template_folder row
                if folder has an `example` directory under examples/<example>/:
                    for each file in that directory (recursive):
                        insert template_file row with the file's relative path + UTF-8 content
        for each file in Templates.seed/modules/*.toml :
            parse the file
            insert module row + module_dependency rows
        for each entry in Templates.seed/catalog/well-known-deps.toml :
            insert well_known_dependency row
    else:
        do nothing
```

The `examples/` directory walk is text-only: every file the seeder picks up is read as UTF-8 and stored verbatim in `template_files.content`. Mustache substitution does **not** happen at seed time — it stays at generation time, where the per-extension context exists. A migration helper backfills `template_files` rows from the on-disk `examples/` tree for any pre-existing `template_folders` row whose legacy `example_path` was set, so existing deployments transition without losing their seeded examples.

The check is "no runtime_templates exist" rather than per-row idempotency — this avoids the complication of trying to merge live edits with seed file changes. If you ever want to re-seed, you do it deliberately by clearing the relevant tables first.

The path to `Templates.seed/` is configurable via the `SEED_PATH` environment variable. In Docker, this points to a path inside the image (the seed files ship with the app); on a developer machine, it points to the repo's `Templates.seed/`.

## Tomlyn usage

Tomlyn deserialises directly into POCOs. Define mirrored types under `Domain/Seed/`:

```csharp
class TemplateSeed {
    public TemplateMetaSeed Template { get; set; }
    public DefaultsSeed Defaults { get; set; }
    public AppSourceCopSeed AppSourceCop { get; set; }
    public List<FolderSeed> Folders { get; set; }
}
class FolderSeed {
    public string Path { get; set; }
    public List<FolderFileSeed> Files { get; set; } = new();  // file contents stored inline
}
class FolderFileSeed {
    public string Path { get; set; }       // relative to the folder, e.g. "AppInstall.Codeunit.al"
    public string Content { get; set; }    // raw file content; mustache substitution runs at generation time
}
// ...etc
```

`Toml.ToModel<TemplateSeed>(text)` does the deserialisation. The `defaults_json` and `app_source_cop_json` columns are populated by re-serialising the relevant sub-objects to JSON.

## Template TOML schema

Each `Templates.seed/runtime-*/template.toml` follows this shape:

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

[defaults.resourceExposurePolicy]
allowDebugging = true
allowDownloadingSource = false
includeSourceInSymbolFile = true

# Required: AppSourceCop.json contents
[appSourceCop]
mandatoryPrefix = "CONIT"
supportedCountries = ["US", "DK"]

# Required: array of folder definitions, in display order. These are emitted
# into the Core extension only (and into the single extension produced by the
# standalone New Extension flow). Module extensions in a generated workspace
# use [[module_folders]] below — see generation-engine.md for why.
[[folders]]
path = "Source/Foundation"

# Optional: file contents seeded into this folder. When empty, the folder
# generates with a single .gitkeep regardless of the include-examples toggle.
# Mustache substitution runs at generation time, not at seed/parse time.
[[folders.files]]
path = "AppInstall.Codeunit.al"
content = """
namespace {{namespace}};

codeunit 90100 "{{prefix}} App Install"
{
    Subtype = Install;
}
"""

[[folders]]
path = "Source/Sales"
# no files — gets .gitkeep when include-examples is off and stays empty when it's on

[[folders]]
path = "Translations"

# Optional: array of folders emitted into every module extension. Empty (or
# omitted) means modules ship with just app.json + AppSourceCop.json + the
# static fallback folders (libs/, permissionsets/, Translations/). Same shape
# as [[folders]]: optional [[module_folders.files]] blocks seed file content.
[[module_folders]]
path = "Source"
```

## Module TOML schema

Each `Templates.seed/modules/<key>.toml`:

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

`Templates.seed/catalog/well-known-deps.toml` is a single file with all well-known deps:

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
| `{{prefix}}`         | mandatoryPrefix from app_source_cop           |
| `{{namespace}}`      | The folder path, dot-separated                |
| `{{guid}}`           | A fresh GUID per call                         |

## TOML as an authoring surface

Both `/admin/templates/new` and `/admin/templates/{key}` expose a TOML editor next to the structured form via a header toggle. The new-template flow opens in TOML mode by default because pasting in a `template.toml` is usually the fastest way to get a fresh template going; the edit flow opens in the structured form because surgical tweaks are what people do most often there.

Saving from TOML mode parses the textarea via Tomlyn, maps it onto the same `TemplateInput` payload the structured form produces, and runs through identical validation. There is no separate code path on the way to the database. The on-disk files under `Templates.seed/` are not touched — that directory is a one-time bootstrap, not a peer store.

The mapper (`Services/TemplateTomlMapper.cs`) is now load-bearing infrastructure rather than seed-time-only glue: schema changes there affect admins directly. Two things to keep in mind when touching it:

- The TOML schema admins type into is the same schema the seed files ship with. Don't fork it.
- A few fields are deliberately not represented in the TOML view: `deprecated` (seed TOML doesn't carry it — flip it from the structured form's checkbox), and per-row folder reordering by drag (express the order by writing the `[[folders]]` blocks in that order; array order is preserved as `ordering`).
- `[[folders.files]]` blocks make the TOML lossless even when folders carry file content. For larger files this gets noisy; the structured editor is usually the more comfortable surface for editing AL source. Both write through the same pipeline, so neither is "more correct" than the other.

## Export to TOML

The admin section should provide a one-click "Export all to TOML" button (under `/admin` or `/admin/templates`) that produces a ZIP containing the current state of the database serialised back into the same TOML structure as the seed folder. This is the safety hatch for:

- Periodic snapshotting into a backup branch.
- Quickly grepping/diffing the entire template state outside the app.
- Disaster recovery if the SQLite file is corrupted (re-seed from the export).

Implementation: walk all active rows, serialise each template/module/catalogue back into the TOML shape, ZIP into `Templates.seed.zip`. Use Tomlyn's `Toml.FromModel(...)` for the inverse direction.

## What is *not* admin-editable

- The static `.gitignore`, `README.md` boilerplate, ruleset JSON, and logo file. These ship as embedded resources under `Resources/` because they're per-deployment branding/policy rather than per-template content. Treat them as code.
- Binary files inside template folders. v1 stores file content as UTF-8 text only; PNGs, ZIPs, or anything else non-text don't have a place in `template_files`. If we need binary template assets later, the likely shape is a separate `template_file_blobs` table or a URL-fetched asset; defer until there's a real ask.
