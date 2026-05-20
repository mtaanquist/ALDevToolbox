# Generation engine

This document specifies what `GenerationService` produces given a runtime template and a project plan.

The generation model is the **unified-extensions** walk (Issue #54): a workspace is N extensions — required template-declared ones, optional template-declared ones the user ticked, and one cloned extension per selected catalogue module — and each emits its own folder. The data-model spec lives in `unified-extensions.md`; this doc covers the algorithm and output layout.

## Inputs

A `ProjectPlan` value object collected from the form:

```csharp
record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,
    string ExtensionPrefix,          // pre-filled from defaults.extension_prefix; user-editable
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    IReadOnlyList<string> SelectedExtensionPaths,  // optional template-declared extensions the user ticked
    IReadOnlyList<string> SelectedModuleKeys       // catalogue modules that contribute cloned extensions
);
```

The walk concatenates the emittable extension list in this order: template-required `WorkspaceExtension` rows (always emitted) → optional template-declared extensions whose `Path` appears in `SelectedExtensionPaths` (in template `Ordering`) → one cloned extension per `SelectedModuleKeys` entry (in selection order). `{{extension_prefix}}` and `{{affix}}` (with `defaults.affixType`) drive mustache substitution. There is no Core-vs-module split; "Core" is just the conventional `path` of the first required template extension. There is no `IncludeForNav` toggle either — ForNAV is a normal catalogue module that templates declare under `[[template.default_modules]]`.

For the **New Extension** flow, the inputs are slightly different — see [Standalone extension generation](#standalone-extension-generation) at the bottom.

## Output

A `Stream` containing a ZIP archive. The caller is responsible for writing it to the HTTP response.

The structure of the ZIP — for a workspace called "Acme Customer" from a template declaring Core + Hotfix extensions, with the Document Capture module selected — looks like:

```
AcmeCustomer/
├── .assets/
│   ├── images/
│   │   └── logo.png
│   └── rulesets/
│       └── Company.ruleset.json
├── Core/                                # path of the first required WorkspaceExtension
│   ├── app.json
│   ├── AppSourceCop.json                # only when template.AppSourceCop.Include = true
│   ├── src/                             # folder from workspace_extension_folders
│   │   └── codeunits/
│   │       └── AppInstall.Codeunit.al   # file from workspace_extension_files; mustache-substituted
│   └── ...
├── Hotfix/                              # path of a second required WorkspaceExtension
│   ├── app.json                         # carries a dependencies[] entry referencing Core's freshly-allocated GUID
│   ├── AppSourceCop.json
│   ├── ...
├── DocumentCapture/                     # path = module.extension_name for the cloned-from-catalogue module
│   ├── app.json                         # implicit deps on Core and Hotfix; module's own deps follow
│   ├── AppSourceCop.json
│   ├── src/                             # folder from module_extension_folders
│   │   └── ...                          # files from module_extension_files
│   └── ...
├── AcmeCustomer.code-workspace
├── .gitignore
└── README.md
```

What the template declares is what the ZIP contains — there are no static fallback folders. A folder declared with no files (and no children) ships with a single `.gitkeep` placeholder so the directory survives the ZIP round-trip.

## Algorithm

```
1. Load the runtime template by key, eagerly including its WorkspaceExtension rows
   plus the recursive folder/file/dep trees. EF's Include doesn't recurse past one
   level — load folders/files via flat queries and reassemble the tree client-side
   (see GenerationService.AssembleFolderTree).
2. Load the selected modules by their keys, similarly including their recursive
   module_extension_folder / module_extension_file trees.
3. Build the EmittableExtension list:
     a. For each template extension (ordered by ordering): include if Required, OR
        if its Path is in plan.SelectedExtensionPaths.
     b. Append one EmittableExtension per selected module (using the module's
        recursive folder tree as its content; the module's `extension_name`
        (PascalCase) becomes the ZIP folder name; the rendered name defaults
        to "{{extension_prefix}} {module.extension_name}"). The module's
        `key` is the admin / URL slug and the dep-ref target, not the folder.
     c. Allocate id ranges in walk order. See "ID range allocation" below.
     d. Resolve each extension's dependencies into freshly-substituted name +
        freshly-allocated GUID. See "Dependency resolution" below.
4. For each EmittableExtension in walk order:
     a. Build app.json (see below).
     b. When template.AppSourceCop.Include = true, build AppSourceCop.json
        from the template's app_source_cop_json. The Include flag itself is
        stripped — it's our authoring concept, not an AL field.
     c. Walk the folder tree depth-first. For each folder, emit its declared
        files (skipping IsExample files when plan.IncludeExamples is false). If
        the folder ends up with no emitted files AND no child folders, drop a
        .gitkeep placeholder so empty directories survive the ZIP round-trip.
5. Generate workspace-root files:
     a. {{short_name}}.code-workspace (see below).
     b. README.md (minimal — workspace name + description).
     c. .gitignore (embedded resource shipped with the app; per-deployment policy).
     d. Per-template-included files from `organization_files`, filtered by
        the `runtime_template_included_files` join — admins opt each template
        into the files that belong with it. Each row's `path` is
        workspace-root-relative; mustache substitution runs when
        `mustache_enabled` is true, using the same context as per-extension
        files. The New Workspace live preview folds the same set into the
        workspace-root tree so what the user sees matches the ZIP.
6. Generate .assets:
     a. images/logo.{png|svg|jpg} — bytes from organization_assets for the
        acting org (M14). The file extension matches the asset's `content_type`.
        Pre-M14 this was an embedded resource; that path is gone.
     b. rulesets/Company.ruleset.json — embedded resource shipped with the app
        (per-deployment policy, not per-org).
7. Stream the whole thing as a ZIP.
```

Validation that doesn't fit the template-save validator (because it depends on plan-time inputs) runs upfront: `ValidateIdRanges` rejects a plan whose extensions resolve to overlapping ranges, surfacing a field-keyed `PlanValidationException`.

## `app.json` construction

For each emittable extension, the `app.json` is built by:

1. Start with the runtime template's `defaults_json` deserialized as the base. `application` and `platform` come from the plan (which pre-fills them from `defaults.application` / `defaults.platform` but lets the user override).
2. Set `id` to a freshly generated GUID — kept on the `EmittableExtension` so dependency resolution can reference it.
3. Set `name` to the extension's `NameTemplate` after mustache substitution. The template default is `"{{extension_prefix}} {extension_path}"` for declared extensions and `"{{extension_prefix}} {module_name}"` for module clones; admins can override either by writing the desired name template directly.
4. Set `brief`, `description` from the project plan.
5. Set `version` to `"0.0.0.1"` for new generations.
6. Set `application` to the extension's per-extension override when set, otherwise the plan's `ApplicationVersion`.
7. Set `runtime` to the extension's per-extension override when set, otherwise the plan's `RuntimeVersion`.
8. Set `idRanges` to a single range computed via the three-layer allocation below.
9. Set `dependencies` via the resolver below.

Serialise with 2-space indent for readability of the generated file.

## `AppSourceCop.json` construction

Deserialise `template.app_source_cop_json`, write it into each extension's folder. The `include` flag on the JSON column gates emission — set it to `false` to suppress the file workspace-wide. The flag is stripped before serialisation since AL would reject an unknown field. No per-extension customisation today.

## `.code-workspace` construction

A JSON file with:

```json
{
    "folders": [
        { "path": "Core" },
        { "path": "Hotfix" },
        { "path": "DocumentCapture" }
    ],
    "settings": {
        "editor.formatOnSave": true,
        "editor.autoIndent": "full",
        "editor.detectIndentation": false,
        "editor.tabSize": 4,
        "editor.insertSpaces": true,
        "al.codeAnalyzers": [
            "${CodeCop}",
            "${AppSourceCop}",
            "${UICop}"
        ],
        "al.enableCodeAnalysis": true,
        "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
    }
}
```

The `settings` block is a static C# string constant in `WorkspaceConfigService`. The `folders` array is dynamic — one entry per emitted extension, using its `path` (which is also the on-disk folder name). Module clones use the module's `key` as the path; spaces don't appear because keys are URL-safe.

## Mustache substitution

Substitution runs on every `.al` file's content, on the rendered extension `name`, on `AppSourceCop.json` strings, and on every workspace-root file from `organization_files` that has `mustache_enabled = true`. Non-AL extension files are written verbatim by default; admins can opt a particular folder/file into substitution explicitly via the authoring surface when they need it.

Available variables (canonical names are snake_case to match the TOML schema):

| Variable                | Source                                                                   |
|-------------------------|--------------------------------------------------------------------------|
| `{{name}}`              | The full extension name, e.g. "Acme Customer Core"                        |
| `{{workspace_name}}`    | The workspace name from the plan, e.g. "Acme Customer"                    |
| `{{short_name}}`        | The workspace name with whitespace removed, e.g. "AcmeCustomer"           |
| `{{module_name}}`       | For module-cloned extensions, the module's `extension_name` (PascalCase). For template-declared extensions, equals `{{name}}`. |
| `{{publisher}}`         | `OrganizationSettings.DefaultPublisher`. |
| `{{extension_prefix}}`  | The plan's `ExtensionPrefix` — the per-workspace short identifier (e.g. "CRO"). |
| `{{affix}}`             | `defaults.affix` when `defaults.affixType` is `Prefix` or `Suffix`; empty string when `AffixType.None`. Replaces the pre-unified `{{prefix}}` / `{{suffix}}`. |
| `{{namespace}}`         | The current folder's path, dot-separated, e.g. `src/codeunits` → `src.codeunits`. Used for AL `namespace` declarations. |
| `{{guid}}`              | A freshly generated GUID per substitution call. Use sparingly — prefer hand-authored GUIDs in example files. |
| `{{tenant_id}}`         | Tenant GUID captured on the New Workspace form (empty for standalone extensions). Persisted in `workspace.aldt.toml` so regeneration is reproducible. |

Legacy camelCase names `{{workspaceName}}`, `{{shortName}}`, `{{moduleName}}` still resolve as aliases — `MustacheRenderer` logs a single warning per render listing any encountered so admins can rename their org files at leisure.

If a variable in the content isn't recognised, leave it as-is (don't crash). Log a warning.

The mustache implementation is intentionally naive — regex replacement of `\{\{(\w+)\}\}` is enough.

## ID range allocation

Each extension gets one `idRanges` entry. Three layers, in priority order:

1. **Explicit on the extension.** If the row carries both `id_range_from` and `id_range_to`, use them verbatim. The cursor does not advance.
2. **Module-supplied size.** For a module-cloned extension, allocate `[cursor, cursor + size - 1]` where `size = module.IdRangeSize ?? template.ModuleIdRangeSize`. Cursor advances past the slice.
3. **Auto-allocated from the template.** The first auto-allocated template-declared extension consumes the plan's `[CoreIdRangeFrom, CoreIdRangeTo]`. Subsequent auto-allocated extensions take a `template.ModuleIdRangeSize`-wide slice from the cursor, which starts at `template.ModuleIdRangeStart` and advances after each consumed slice.

So for a template with `module_id_range_start = 91000` and `module_id_range_size = 200`, and a workspace selecting Core (auto), Hotfix (auto), and the Document Capture module:

- Core (first auto): `90000..90999` from the plan.
- Hotfix (next auto): `91000..91199` from the cursor.
- Document Capture (module, default size 200): `91200..91399`.

Validation: the resolved ranges must not overlap. `ValidateIdRanges` checks this up front and throws `PlanValidationException` with a field-keyed `Extensions[*].IdRange` error pointing at the colliding pair.

## Dependency resolution

`workspace_extension_dependencies` rows are emitted in `Ordering`, after any implicit deps the resolver injects. Three reference shapes (the CHECK constraint enforces exactly one per row):

- **`ref_extension_path`** — intra-workspace reference to another extension. The resolver looks up the target's freshly-allocated GUID and substituted name and emits a dep node. A reference to a `path` that isn't in this workspace (optional but not selected, or a typo) fails fast with `PlanValidationException`.
- **`ref_module_key`** — catalogue reference. When the named module was also selected for this workspace, point at its cloned extension's GUID. Otherwise the dep is silently dropped; the template-save validator should have rejected dangling references already.
- **`lit_id` + `lit_name` + `lit_publisher` + `lit_version`** — literal AL app coordinates. Emitted as-is.

**Implicit deps.** Module-cloned extensions get an implicit dependency on every required template-declared extension (Core, Hotfix, etc.) prepended before their own declared deps. So a Document Capture module clone in a workspace with required Core and Hotfix extensions ships with `dependencies = [Core, Hotfix, ...module's own]` even when the module's own declarations don't mention them. This lets the AL compiler see them without the template author having to spell it out.

Resolution is by stable identifier (`path` or `key`), never display name — display names get mustache-substituted and would drift between save time and generation time.

## Standalone extension generation

The New Extension flow uses the same `GenerationService` but with a simpler input:

```csharp
record StandaloneExtensionPlan(
    string TemplateKey,
    string ExtensionName,           // user-supplied freeform
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int IdRangeFrom,
    int IdRangeTo,
    bool IncludeExamples,
    string Publisher,                // editable, defaults to template's publisher
    IReadOnlyList<DependencyEntry> Dependencies
);

record DependencyEntry(
    string DepId, string DepName, string DepPublisher, string DepVersion
);
```

The output is a single folder (no workspace wrapper, no `.code-workspace`, no `.assets/`), zipped:

```
MyExtension/
├── app.json
├── AppSourceCop.json
├── libs/.gitkeep
├── permissionsets/.gitkeep
├── src/...                       # folder structure from the template's first extension
└── Translations/.gitkeep
```

The folder structure inside the extension reuses the template's **first** `WorkspaceExtension` row as the scaffold (typically the conventional `Core` extension). All other declared extensions / modules / dependencies are ignored: the user is dropping the result into an existing workspace and wants only a self-contained extension shell. The `app.json` `dependencies` array comes from the user-supplied list rather than the template; if they want to depend on something Core-like in their existing workspace, they pick it via the dependency picker that sources from `well_known_dependencies`.

The success page should explain "drop this folder into your existing workspace and add a corresponding entry to `.code-workspace` yourself" clearly, with a copy-pasteable line for the workspace `folders` array.

## Error handling

- Invalid `ProjectPlan` (validation failed) → throw `PlanValidationException` with a dictionary of field-keyed errors. The page catches this and renders inline next to the offending field.
- IO error mid-stream → propagate. The user gets a server error page; the audit log captures nothing because nothing was written.

(There is no "missing example file" warning case any more: file content lives in the DB alongside the folder row, so it can't go missing relative to the row.)

## What gets logged

Each successful generation logs (at `Information` level) via structured fields:

- Workspace name (or standalone extension name).
- Template key.
- The walk order of emitted extension paths.
- Generated file count.
- ZIP size in bytes.
- Duration.

This is for operational telemetry, not user-facing. No need to persist it — `ILogger` output is enough.
