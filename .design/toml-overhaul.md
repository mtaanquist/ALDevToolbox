# TOML overhaul — continuation notes (Issue #54)

A working doc for the **next** session to pick up the unified-extensions work
where this one left off. The spec for the data model itself is in
`unified-extensions.md`; this file is the implementation-state companion.

**Branch:** `claude/issue-54-R6lFE`
**PR:** [#56](https://github.com/mtaanquist/ALDevToolbox/pull/56)
**Last commit on branch:** see `git log claude/issue-54-R6lFE`

## Acceptance-criteria status

From the issue body:

| Criterion | Status |
|---|---|
| EF migration applies cleanly forward; CI green | done |
| New tests for recursive folder tree, modules-as-extension-template clone, deps by stable id, `AffixType.None` | done |
| Migration data-shape rewrite covered by a test | done — see `ALDevToolbox.Tests/Migrations/UnifyExtensionsDataMigrationTests.cs`. The migration's PL/pgSQL block is now exposed as `UnifyExtensions.DataRewriteSql` so the test can replay it verbatim against an in-test pre-unified fixture. |
| Real customer-style TOML round-trips through mapper **and generates a ZIP** | done — see `ALDevToolbox.Tests/Generation/WorkspaceEndToEndTests.cs`. Customer-style TOML parses through `TemplateTomlMapper.FromToml`, persists via `TemplateService.CreateAsync(TemplateAuthoring)`, generates via `GenerationService.GenerateWorkspaceAsync`. The test pins per-extension `app.json` layout, idRange allocation across all three layers, all three dependency reference shapes (`extension =`, `module =`, literal), and module-clone implicit deps on required template extensions. |
| Three impacted design docs rewritten with no stale references | done — `domain-model.md`, `generation-engine.md`, and `templates-and-seeding.md` rewritten cover-to-cover for the unified-extensions shape. Issue #54 transition banners removed; the bodies now describe the new tables, the per-extension walk, and the `[[extensions]]` TOML schema as the canonical shape. |
| CLAUDE.md fences still hold | yes — Postgres-as-source-of-truth, multi-tenant, synchronous generation, no client framework all intact |

## What's in the branch already

### Schema + data layer
- New entities: `WorkspaceExtension`, `WorkspaceExtensionFolder` (self-FK,
  recursive), `WorkspaceExtensionFile`, `WorkspaceExtensionDependency` (three-way
  ref with a CHECK constraint), `ModuleExtensionFolder`, `ModuleExtensionFile`,
  `AffixType` enum. Pre-unified `TemplateFolder` / `TemplateFile` /
  `TemplateModuleFolder` / `TemplateModuleFile` are gone.
- `RuntimeTemplate` shed `Folders` / `ModuleFolders` / `DefaultApplication` /
  `DefaultPlatform`; gained `WorkspaceExtensions`. `Module` gained
  `ExtensionFolders`. `TemplateDefaults` gained `Application` / `Platform` /
  `ExtensionPrefix` / `Affix` / `AffixType`. `ProjectPlan` gained
  `ExtensionPrefix` + `SelectedExtensionPaths`; lost `IncludeForNav`.
- `AppDbContext` configures the six new tables, including the partial unique
  indexes (`(parent_folder_id, path) WHERE parent_folder_id IS NOT NULL` and
  `(workspace_extension_id, path) WHERE parent_folder_id IS NULL`) and the
  dependency CHECK constraint (added in raw SQL in the migration since EF
  doesn't have a fluent API for it).
- Migration `20260514000000_UnifyExtensions`: forward-only (`Down` throws),
  data-rewrites pre-unified rows into the new shape, folds
  `default_application` / `default_platform` into `defaults_json`, rewrites
  `{{prefix}}` / `{{suffix}}` → `{{affix}}` across all content columns, and
  stamps `defaults.affix` / `defaults.affixType` from each template's existing
  `AppSourceCop.mandatoryPrefix`. PL/pgSQL block in `Up()`.

### Algorithm layer
- `TemplateTomlMapper` rewritten around `[[extensions]]` + recursive
  `[[extensions.folders.folders]]` + `[[extensions.dependencies]]` +
  `[[template.default_modules]]`. Hand-emits the recursive folder blocks
  because Tomlyn's reflection serialiser produces inline arrays.
- `GenerationService` rewritten as a per-extension walk: required template
  extensions → user-selected optionals → one cloned extension per selected
  module. ID-range allocation has three layers (explicit > module size >
  template auto). Mustache table is `{{name}}`, `{{workspaceName}}`,
  `{{shortName}}`, `{{moduleName}}`, `{{publisher}}`, `{{extension_prefix}}`,
  `{{affix}}`, `{{namespace}}`, `{{guid}}` (drops `{{prefix}}` / `{{suffix}}`).
  Overlapping ranges raise `PlanValidationException`.

### Service writes
- `TemplateService.CreateAsync(TemplateAuthoring)` /
  `UpdateAsync(int, TemplateAuthoring)` persist the full graph. Update is
  wipe-and-rebuild for the extension tree; default-modules join row reconciles
  by `ModuleId` (the natural identity), **not** by list position, because
  position-based mutation would trip the `(template_id, module_id)` unique
  index on a swap (EF detects the cycle and refuses). Validator emits
  field-keyed errors for path segment rules, sibling uniqueness, dependency
  one-of, and intra-template extension resolution.
- Legacy `TemplateInput` overloads kept as throwing stubs so the unmigrated
  structured admin form compiles; its catch block surfaces a friendly "use
  the TOML tab" nudge.
- `TemplateImportService` clones `WorkspaceExtension` + its recursive folder/
  file/dep tree, and `EnsureModuleAsync` clones the module's
  `ModuleExtensionFolders` tree. Recursive folder reads use a flat-query +
  client-side reassembly pattern (see below).

### Surfaces wired
- Admin TOML editor save path: `AdminTemplateEdit.SaveTomlAsync` calls
  `TemplateService.CreateAsync(TemplateAuthoring)` / `UpdateAsync` for real.
  Round-trips back through `ToToml` on success.
- New Workspace form: `ExtensionPrefix` input (pre-fills from
  `defaults.extension_prefix`), "Additional extensions" checkbox list for
  optional template-declared extensions, `IncludeForNav` removed.

### Tests in place
- `Domain.Tests.Extensions.UnifiedExtensionsShapeTests` — pins the entity
  shape (recursive folder tree, three-way dep, `AffixType.None`, module
  folder mirror).
- `Generation.WorkspaceGenerationTests` — 13 end-to-end tests covering the
  per-extension walk, module-clone implicit-Core-dep, recursive folder
  layout, example filtering, `{{affix}}` + `{{extension_prefix}}`
  substitution, extension/literal/module dependency resolution, id-range
  override + auto-allocate + module-size override, overlap detection.
- `Toml.TemplateTomlMapperRoundTripTests` — 11 round-trip tests covering
  metadata, defaults, ordered required/optional extensions, recursive
  folder tree with files at any depth, `IsExample`, all three dep
  reference shapes, default modules, id-range overrides, bare runtime
  values, BlankToml starter.
- `Toml.TemplateTomlMapperToleranceTests` — three tests pinning the
  customer-style template (Core + Hotfix + module-cloned Document Capture
  with recursive folder trees and a module-key dep) parses cleanly.
- `Templates.TemplateImportServiceTests` — five tests covering end-to-end
  cross-org clone, key-clash refusal, system-org refusal, local module
  reuse, ListSystemTemplates marking already-imported.
- `Templates.TemplateServiceWriteSideTests` (in the
  `TemplateServiceReconciliationTests.cs` file) — seven tests covering
  Create persistence, Update cascade rebuild, default-modules PK stability
  on swap, three validator paths, legacy-overload throws.
- Two new generator/mapper tests files coexist with three legacy `#if false`
  fenced test files (see "Pure cleanup" below).

## What's left

The two AC-critical bullets and the design-doc rewrite landed in the
continuation session. Remaining items are housekeeping and a follow-up PR.
In rough priority order:

### 1. Test cleanup: retire the `#if false` files

These four files have their coverage fully replaced by the new tests:

- `ALDevToolbox.Tests/Generation/MustacheSubstitutionTests.cs` — superseded
  by `WorkspaceGenerationTests`'s `Affix_*` and `Extension_prefix_*` tests.
- `ALDevToolbox.Tests/Generation/IdRangeAllocationTests.cs` — superseded
  by the id-range tests in `WorkspaceGenerationTests`.
- `ALDevToolbox.Tests/Toml/TemplateTomlMapperTests.cs` — superseded by
  `TemplateTomlMapperRoundTripTests` + `TemplateTomlMapperToleranceTests`.
- The two `#if false`-style commented-out tests inside
  `ALDevToolbox.Tests/Audit/AuditInterceptorTests.cs` (snapshot-inline and
  content-hash) — superseded conceptually by the new audit story for
  `WorkspaceExtensionFile`/`ModuleExtensionFile`; an analogous pair of
  tests should be added back using the new types. Either rewrite or
  delete with a note.

Pure deletion is fine for the three Generation/Toml files. For the
AuditInterceptorTests pair, either rewrite (~30 min) or delete with a
follow-on issue.

### 2. Recursive preview in the UI

Currently stubbed:

- `Components/Pages/TemplateDetail.razor` — `BuildTree` returns a single
  empty `PreviewNode.Extension(template.Key, [])` placeholder. Walk
  `template.WorkspaceExtensions` and each extension's recursive folder
  tree.
- `Components/Pages/NewWorkspace.razor` — `BuildExtensionNode` only emits
  `app.json` + `AppSourceCop.json` + fallback folders. Walk the active
  template's extensions / their folder trees.
- `Components/Pages/NewExtension.razor` — same.

These are UI polish — functionality works without them, the preview just
shows a placeholder structure. Not blocking; nice to have.

### 3. Structured admin folder/file editor UI (deferred)

The big remaining piece. `Components/Pages/Admin/AdminTemplateEdit.razor`'s
`FormState` carries flat-path `Folders` / `ModuleFolders` collections with
`FolderRow` / `FileRow` shapes that don't fit the recursive tree. Saving
from the structured form path throws `NotImplementedException` (the
service overload throws); the page catches it with a "use the TOML tab"
message.

The proper rewrite would be a tree editor component
(`Components/Shared/RecursiveFolderEditor.razor` maybe) that lets admins
add / remove / nest folders and attach files at any depth. Probably its
own PR — material UI work, not a polish pass. The TOML pane is the working
authoring path until then.

## Gotchas worth knowing

These bit me during the implementation; flagging so the next session
doesn't relearn them.

### `WorkspaceExtensionId` / `ModuleId` denormalisation needs propagation

The `workspace_extension_folders` and `module_extension_folders` tables
carry the parent extension / module id on every row (denormalised — see
`unified-extensions.md`'s "Schema changes" section for the reasoning).
EF wires the top-level folder's FK automatically via the
`WorkspaceExtension.Folders` collection navigation, but **nested folders
only have a parent navigation** — EF doesn't propagate the
extension/module id past the first hop.

The fix is in `AppDbContext.SaveChanges` / `SaveChangesAsync`: a hook
walks the parent chain of every added/modified folder and copies the
root's nav reference down before the save runs. See
`AppDbContext.PropagateExtensionFolderIds`.

### Default-modules reconciler can't mutate `ModuleId` in place

The `(runtime_template_id, module_id)` unique index on
`runtime_template_default_modules` blocks any update batch where two
rows swap their `module_id` values. EF detects the cycle and throws
`Unable to save changes because a circular dependency was detected`.

The fix is to match existing rows by `ModuleId` (the natural identity),
not by list position. See `TemplateService.ReconcileDefaultModules`.
Reorders rewrite the non-unique `Ordering` column; `ModuleId` stays put
on each row.

### EF doesn't recurse on `Include`; use flat queries + client-side reassembly

Loading a recursive folder tree via `Include(t => t.WorkspaceExtensions)
.ThenInclude(e => e.Folders).ThenInclude(f => f.Folders)` only goes two
levels deep. The pattern that works:

1. Single flat query for all folders under the relevant extensions
   (filter by `WorkspaceExtensionId IN (...)`).
2. Single flat query for all files under those folders.
3. Client-side: dictionary by `Id`, attach each folder to its parent's
   `Folders` collection (or the extension's `Folders` if root), attach
   each file to its folder's `Files` collection.

Both `GenerationService.AssembleFolderTree` and
`TemplateImportService.HydrateExtensionTreeAsync` use this pattern.
With `AsNoTracking()`, EF's change-tracker fixup doesn't run, so the
client-side reassembly is mandatory there.

### Tomlyn silently drops unknown keys

The `TemplateSeed` POCO doesn't have to model every possible TOML key —
Tomlyn's `TomlSerializer.Deserialize` with the System.Text.Json-style API
ignores unknown properties by default. The customer template's
pre-unified leftovers (`default_application` at `[template]` level, the
`[[defaults.modules]]` array, the `[workspace]` block) drop without
complaint. Useful for ingest tolerance; not something to rely on for
*forward* validation (i.e. don't expect Tomlyn to flag typos).

### TemplateAuthoring vs TemplateInput

The form-binding `TemplateInput` record (legacy structured form) and the
new `TemplateAuthoring` record (TOML editor output) are deliberately
separate types. Both `TemplateService.CreateAsync` / `UpdateAsync` have
two overloads — one per type. The `TemplateInput` overloads throw
`NotImplementedException` until the structured form editor is rewritten.

When the structured editor lands (item 3 above), the cleanest move is to
delete `TemplateInput` and its overloads, then route the form's
`ToInput()` directly to `TemplateAuthoring`.

## How to pick up

1. `git checkout claude/issue-54-R6lFE && git pull`
2. Read `.design/unified-extensions.md` for the spec.
3. Read this file for the implementation state.
4. `dotnet test` to confirm CI-green baseline locally.
5. Pick an item from "What's left" — all three are housekeeping the PR
   description can call out as follow-ups, with item 3 (structured editor)
   being its own PR's worth of work.
6. Watch CI on PR #56 via the `subscribe_pr_activity` MCP tool.
