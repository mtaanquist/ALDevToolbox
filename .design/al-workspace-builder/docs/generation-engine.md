# Generation engine

This document specifies what `GenerationService` produces given a runtime template and a project plan.

## Inputs

A `ProjectPlan` value object collected from the form. The shape:

```csharp
record ProjectPlan(
    string TemplateKey,         // selects the runtime template
    string WorkspaceName,       // e.g. "Acme Customer"
    string Brief,
    string Description,
    string ApplicationVersion,  // user may have overridden the template default
    string RuntimeVersion,      // e.g. "15.0" — derived from template, but editable
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    bool IncludeForNav,
    IReadOnlyList<string> SelectedModuleKeys
);
```

For the **New Extension** flow, the inputs are slightly different — see [Standalone extension generation](#standalone-extension-generation) at the bottom.

## Output

A `Stream` containing a ZIP archive. The caller is responsible for writing it to the HTTP response.

The structure of the ZIP — for a workspace called "Acme Customer" with Document Capture and Payment Management selected — should look like:

```
AcmeCustomer/
├── .assets/
│   ├── images/
│   │   └── logo.png
│   └── rulesets/
│       └── Company.ruleset.json
├── Core/
│   ├── app.json
│   ├── AppSourceCop.json
│   ├── libs/
│   │   └── .gitkeep
│   ├── permissionsets/
│   │   └── .gitkeep
│   ├── Source/                          # folders defined by the runtime template
│   │   ├── Foundation/
│   │   │   ├── AppInstall.Codeunit.al   # if IncludeExamples = true
│   │   │   └── AppUpgrade.Codeunit.al
│   │   ├── Finance/
│   │   │   └── .gitkeep                  # if IncludeExamples = false
│   │   └── ...
│   └── Translations/
│       └── .gitkeep
├── DocumentCapture/
│   ├── app.json
│   ├── AppSourceCop.json
│   ├── libs/.gitkeep
│   ├── permissionsets/.gitkeep
│   ├── Source/
│   │   └── ...                          # same folder structure as Core
│   └── Translations/.gitkeep
├── PaymentManagement/
│   ├── ...
├── AcmeCustomer.code-workspace
├── .gitignore
└── README.md
```

## Algorithm

```
1. Load the runtime template by key.
2. Load all selected modules with their dependencies.
3. Generate Core extension:
     a. Build app.json from template.defaults + project plan
     b. Build AppSourceCop.json from template.app_source_cop
     c. Walk template.folders — for each:
          if folder.example_path is set AND IncludeExamples is true:
              copy each file from Templates.seed/<runtime>/examples/<example_path>/
              run mustache substitution on each .al file's contents
          else:
              create the folder with a .gitkeep file
     d. Add libs/.gitkeep, permissionsets/.gitkeep, translations/.gitkeep
4. For each selected module (in order):
     a. Compute its ID range from template.module_id_range_start + (index * module_id_range_size)
     b. Build app.json including:
          - dependencies = [Core] ++ module's own dependencies
          - the computed ID range
     c. Same folder/example process as Core
5. Generate root files:
     a. .gitignore (static — see template-and-seeding.md for content; lives as a string constant or embedded resource)
     b. {WorkspaceName}.code-workspace (see below)
     c. README.md (minimal — workspace name + description)
6. Generate .assets:
     a. images/logo.png — embedded resource shipped with the app
     b. rulesets/Company.ruleset.json — embedded resource shipped with the app
7. Stream the whole thing as a ZIP.
```

## `app.json` construction

For each extension (Core or module), the `app.json` is built by:

1. Start with the runtime template's `defaults_json` deserialized as the base.
2. Set `id` to a freshly generated GUID.
3. Set `name`:
   - For Core: `"{WorkspaceName} Core"` (e.g. "Acme Customer Core").
   - For modules: `"{WorkspaceName} {ModuleName}"` (e.g. "Acme Customer Document Capture").
4. Set `brief`, `description` from the project plan.
5. Set `version` to `"0.0.0.1"` for new generations.
6. Set `application` to project plan's `ApplicationVersion` (which defaults to `template.default_application` but the user can override).
7. Set `runtime` to project plan's `RuntimeVersion`.
8. Set `idRanges` to a single range:
   - Core: `[{ from: CoreIdRangeFrom, to: CoreIdRangeTo }]`
   - Module: computed (see step 4a in the algorithm).
9. Set `dependencies`:
   - Core: empty array, OR if `IncludeForNav` is true, add the ForNAV entries from the well-known catalogue (look up by category="ForNAV" or by a hardcoded set of GUIDs — implementer's choice).
   - Modules: prepend a self-reference to Core (with Core's freshly generated id, name, publisher), then append the module's own dependencies from `module_dependencies` table.

Serialise with 2-space indent for readability of the generated file.

## `AppSourceCop.json` construction

This is the simpler of the two — just deserialise `template.app_source_cop_json` and write it as-is into each extension's folder. No per-extension customisation.

## `.code-workspace` construction

A JSON file with:

```json
{
    "folders": [
        { "path": "Core" },
        { "path": "DocumentCapture" },
        { "path": "PaymentManagement" }
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

The `settings` block can live as a static C# string constant. The `folders` array is dynamic.

Module folder paths in the workspace match the on-disk folder names — module names with spaces collapse: `"Document Capture"` → `"DocumentCapture"`. This matches the existing tool's behaviour (see `script.js`'s `module.replace(/\s+/g, '')`).

## Mustache substitution

When `IncludeExamples` is true and example AL files are copied from `Templates.seed/<runtime>/examples/`, run mustache substitution on the file contents. Available variables:

| Variable             | Source                                            |
|----------------------|---------------------------------------------------|
| `{{name}}`           | The full extension name, e.g. "Acme Customer Core" |
| `{{workspaceName}}`  | The workspace name from the form, e.g. "Acme Customer" |
| `{{shortName}}`      | The workspace name with spaces removed, e.g. "AcmeCustomer" |
| `{{moduleName}}`     | For module extensions, the module's name. For Core, equals `{{workspaceName}} Core`. |
| `{{publisher}}`      | The publisher field from `defaults_json`. Default "Consortio IT". |
| `{{prefix}}`         | The `mandatoryPrefix` from `app_source_cop_json`. Default "CONIT". |
| `{{namespace}}`      | The current folder's path, dot-separated. e.g. "Source/Foundation" → "Source.Foundation". Used for AL `namespace` declarations. |
| `{{guid}}`           | A freshly generated GUID per substitution call. Use sparingly — prefer letting the implementer hand-author GUIDs in example files. |

If a variable in the template isn't recognised, leave it as-is (don't crash). Log a warning.

The mustache implementation can be naive — regex replacement of `\{\{(\w+)\}\}` is enough. Don't pull in a full mustache library for this.

## ID range allocation

For modules, the ID range allocation is purely positional based on the order modules are selected:

```csharp
var rangeStart = template.ModuleIdRangeStart + (moduleIndex * (module.IdRangeSize ?? template.ModuleIdRangeSize));
var rangeEnd = rangeStart + (module.IdRangeSize ?? template.ModuleIdRangeSize) - 1;
```

So for a template with `module_id_range_start = 91000` and `module_id_range_size = 200`:

- Module index 0 (first selected): 91000–91199
- Module index 1: 91200–91399
- Module index 2: 91400–91599

If a module has its own `id_range_size` override (e.g. JobManager wants 500 IDs), it's applied for *that* module's range, but the next module starts where the previous one ended. Be careful with the running offset — it's not just `moduleIndex * defaultSize` if any module has an override.

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

The output is a single folder (no workspace wrapper, no .code-workspace file, no .assets folder), zipped:

```
MyExtension/
├── app.json
├── AppSourceCop.json
├── libs/.gitkeep
├── permissionsets/.gitkeep
├── Source/...                       # same template folder structure
└── Translations/.gitkeep
```

The folder structure inside the extension still comes from the chosen runtime template. The user is expected to drop this folder into an existing workspace and add a corresponding entry to their `.code-workspace` file themselves. The success page should explain this clearly with a copy-pasteable line for the workspace `folders` array.

The `app.json` `dependencies` array is built from the user's selected dependencies — there's no automatic "depend on Core" because we don't know what Core looks like in their existing workspace. If they want to depend on Core, they enter it manually using the manual-dep form.

## Error handling

- Invalid `ProjectPlan` (validation failed) → throw a `ValidationException`-style exception with a list of field-keyed errors. The page catches this and renders inline.
- Missing example file referenced from a `template_folder` → log a warning, skip the file, continue. Don't fail the whole generation over a missing example.
- IO error mid-stream → propagate. The user gets a server error page; the audit log captures nothing because nothing was written.

## What gets logged

Each successful generation logs (at Info level):
- Workspace name (or extension name)
- Template key
- Module keys selected (or dependencies for standalone)
- Generated file count
- ZIP size in bytes
- Duration

This is for operational telemetry, not user-facing. No need to persist it — `ILogger` output is enough.
