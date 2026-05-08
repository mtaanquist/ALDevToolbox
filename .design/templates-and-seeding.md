# Templates and seeding

## Where templates live

**At runtime:** in the SQLite database. Templates, modules, and catalogue entries are stored as rows in the tables described in `domain-model.md`. The admin UI is the canonical way to edit them.

**At seed time:** in `Templates.seed/`, a sibling directory to `docs/`. This folder contains TOML files that are imported into the database the first time the app runs against an empty database. After that, the seed files are ignored — they're not a fallback or a sync source.

This split exists so:
- The repo has a sensible "starting point" that's source-controlled and reviewable.
- Day-to-day edits don't require a PR + redeploy.
- Backups / snapshots can be exported back to TOML if anyone wants the source-control workflow back temporarily.

## Seed strategy

The `SeedService` runs once at app startup. Pseudocode:

```
on app start:
    if database has no runtime_templates rows:
        for each subfolder of Templates.seed/runtime-* :
            parse template.toml
            insert runtime_template row
            insert template_folder rows
        for each file in Templates.seed/modules/*.toml :
            parse the file
            insert module row + module_dependency rows
        for each entry in Templates.seed/catalog/well-known-deps.toml :
            insert well_known_dependency row
    else:
        do nothing
```

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
    public string Example { get; set; }   // optional
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

# Required: array of folder definitions, in display order
[[folders]]
path = "Source/Foundation"
example = "Foundation"           # optional; resolves to Templates.seed/runtime-15/examples/Foundation/

[[folders]]
path = "Source/Finance"
example = "Finance"

[[folders]]
path = "Source/Sales"
# no example field — gets .gitkeep regardless of include-examples toggle

[[folders]]
path = "Translations"
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

## Editing a single template as TOML

The admin template edit page (`/admin/templates/{key}`) offers a TOML view alongside the structured form. It's a per-template editor surface, not a sync mechanism: the DB remains the source of truth. Saving from TOML mode parses the textarea, maps it onto the same `TemplateInput` payload the structured form produces, and runs through identical validation. The on-disk seed files under `Templates.seed/` are not touched.

A few fields are deliberately not represented in the TOML view (and have to be toggled from the structured form):

- `deprecated` — seed TOML doesn't carry it, so admins flip it from the structured form's checkbox before / after a TOML save.
- Folder reordering by drag — express the desired order by writing the `[[folders]]` blocks in that order; the parser treats array order as ordering.

This was added because directly authoring TOML is sometimes faster for bulk folder edits than clicking through the structured editor. If the TOML view ever drifts from the seed format the seed files ship with, fix the mapper, not the seed schema — interoperability with `Templates.seed/` is the whole point.

## Export to TOML

The admin section should provide a one-click "Export all to TOML" button (under `/admin` or `/admin/templates`) that produces a ZIP containing the current state of the database serialised back into the same TOML structure as the seed folder. This is the safety hatch for:

- Periodic snapshotting into a backup branch.
- Quickly grepping/diffing the entire template state outside the app.
- Disaster recovery if the SQLite file is corrupted (re-seed from the export).

Implementation: walk all active rows, serialise each template/module/catalogue back into the TOML shape, ZIP into `Templates.seed.zip`. Use Tomlyn's `Toml.FromModel(...)` for the inverse direction.

## What is *not* TOML-editable

- Example AL file *contents* — those live as actual `.al` files under `Templates.seed/<runtime>/examples/`. They ship with the app, are not stored in SQLite, are not editable through the admin UI. Changing them is a code change and a redeploy. This is intentional: a SQLite admin UI is not the right place for arbitrary AL source.
- The static `.gitignore`, `README.md` boilerplate, ruleset JSON, and logo file. These ship as embedded resources. Treat them as code.
