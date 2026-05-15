# Object Explorer ingest and reference resolution

This document specifies how the Object Explorer ingests Business Central application content and how it resolves "Find references" queries across the imported corpus. It supersedes the ad-hoc model implied by the current `base_app_files` / `base_app_symbols` / `base_app_versions` tables once implemented.

**Status:** proposal. No code in this doc has been written. Once approved, it replaces the existing ingest path (which today imports a single Base App source ZIP per `BaseAppVersion`) with a multi-app, symbol-aware pipeline. The current single-source-ZIP path stays as a fallback for partner apps that ship without a `.app` symbol package.

## Why

Today the Object Explorer imports one "Base Application" source ZIP per BC version and runs find-references as a name-only regex over file contents. That has two known limits:

1. **Name-only matching is wrong.** `ErrorInfo.Create(...)` shows up as a reference to any codeunit's `Create` procedure; every table's `Code` field surfaces every other table's same-named declaration. The receiver-awareness band-aid (separate small PR) closes part of this gap by inspecting the call site's qualifier, but the underlying model still has no way to distinguish "different objects that happen to share an identifier".
2. **Single-app-per-version is wrong for BC.** A real BC deployment is dozens to hundreds of `.app` modules: Base Application, System Application, Business Foundation, every localisation, every Microsoft first-party extension (Shopify Connector, Sales & Inventory Forecast, AI Test Toolkit, …), every partner app, every customer customisation. A cross-app reference search — "where in everything you've imported does anyone call `Codeunit "Sales-Post"`?" — is exactly what VS Code can't do because VS Code only sees one workspace. That's the feature the toolbox should own.

The `.app` archive is the right ingest format because it carries a `SymbolReference.json` with **fully resolved type metadata**: every variable's `TypeDefinition.Subtype = (ModuleId, Id, Name)` triplet uniquely identifies the target object across the entire BC ecosystem. Ingesting `.app` files turns receiver resolution from a regex heuristic into a SQL join.

## Scope

In scope:

- A new ingest endpoint that accepts a single archive containing many `.app` (and optionally `.Source.zip`) files arranged as a BC DVD's `applications/` folder.
- New entities — `Release`, `Module`, `ModuleObject`, `ModuleSymbol`, `ModuleVariable`, `ModuleReference`, `ModuleFile` — replacing the current `base_app_versions` / `base_app_files` / `base_app_symbols` tables. The current tables are dropped in the migration; the data they hold (one BC version of source-ZIP-only imports) is not preserved.
- Cross-app reference resolution via the symbol package: a Find references on `Codeunit X` returns exact hits from every module's variables, properties, and extension targets that name X by ModuleId+Id, plus source-scanned intra-module hits.
- UI shift to a three-level hierarchy: **Release › Module › Object**.
- A keep-list policy for what we actually store from each `.app` (source `.al` files yes; `.rdlc` / `.xlf` / images / `.pdf` no — see Storage policy).

Out of scope:

- Anything beyond reference and structural queries. We're not building a full IntelliSense, not implementing semantic refactoring, not handling AL projects under active development.
- Compiling AL. The `.app` is the input; we read what the compiler already produced.
- Source-only imports going away. Partner apps without `.app` files (or with `IncludeSourceInSymbolFile="false"` and no separate source ZIP) still need a heuristic path. The receiver-aware band-aid plan covers that case and stays in service.
- Editable corpus. Modules are read-only once imported — re-upload replaces, not patches.

## Input shape

The DVD's `applications/` folder is the canonical input. A representative slice (from a BC 25 DVD):

```
applications/
├── BaseApp/Source/
│   ├── Microsoft_Base Application.app
│   ├── Base Application.Source.zip
│   ├── Microsoft_Danish language (Denmark).app       ← translation-only
│   └── … 20 more language packs
├── BaseApp/Test/                                     ← skipped (tests)
├── DKCore/Source/
│   ├── Microsoft_DK Core.app
│   └── DK Core.Source.zip
├── DKCore/Test/                                      ← skipped
├── OIOUBL/Source/
│   ├── Microsoft_OIOUBL.app
│   └── OIOUBL.Source.zip
├── EDocumentConnectors/Avalara/Source/               ← nested sub-product
├── EDocumentConnectors/Logiq/Source/
├── APIV1/Source/
│   ├── Microsoft__Exclude_APIV1_.app                 ← `_Exclude_` prefix; ingested anyway
│   └── _Exclude_APIV1_.Source.zip
├── testframework/                                    ← entire subtree skipped (tests)
└── …
```

Full real-world DVD is ~500 MB compressed. A `.app` file is a ZIP with a 40-byte `NAVX` header prefix you have to strip; the inner ZIP contains `NavxManifest.xml`, `SymbolReference.json`, an embedded `src/` source tree (when `IncludeSourceInSymbolFile="true"`), translations, layouts, and the entitlement XML.

### What we filter

| Path pattern (case-insensitive) | Action |
|---|---|
| Any segment matching `Test` / `test` / `Test Library` / `Test Libraries` | Skip whole subtree |
| `testframework/` | Skip whole subtree |
| `_Exclude_` prefix on `.app` filename | **Ingest as normal** (per Decision 4). They're real platform modules used in production debugging. |
| Translation-only language packs (no codeunits/tables/pages in `SymbolReference.json`) | Ingest as bare `Module` rows so the dependency graph is closed, but don't extract per-object detail or store source. |

The filename `_Exclude_` prefix and Microsoft's `Target="OnPrem"` / `Target="Cloud"` distinction don't affect ingest behaviour. They're surfaced on the `Module` row so the UI can filter if needed.

## Storage policy

The user's constraint: we don't need to store everything inside an `.app` — only what's required to answer reference queries and render the file viewer. That cuts the corpus size dramatically.

| Artifact | Stored? | Why |
|---|---|---|
| `NavxManifest.xml` (parsed) | Yes — as columns on `Module` | AppId, name, publisher, version, dependencies, target, resource-exposure flags. |
| `SymbolReference.json` (parsed) | Yes — fanned out into `ModuleObject` / `ModuleSymbol` / `ModuleVariable` / `ModuleReference` rows | The reason we exist. |
| `.al` source files | Yes — full content in `ModuleFile.content` | Powers the file viewer and the intra-module reference scanner. Prefer the `src/` embedded in the `.app`; fall back to the `.Source.zip` when source isn't bundled. |
| `.rdlc` report layouts | No | Often the biggest payload per `.app` (the DK Core sample has a 740 KB layout). Not referenced by any code, not browsed in the Object Explorer. |
| Translation `.xlf` files | No | Not addressable from AL code. |
| `logo/`, images, `entitlement/*.xml`, `MediaIdListing.xml`, `[Content_Types].xml`, `navigation.xml`, `DocComments.xml` | No | None contribute to reference search or file viewing. |

Approximate corpus footprint after filtering for a full BC 25 DVD: order of 200–300 MB of `.al` source text plus a comparable amount of resolved-symbol rows. The 500 MB upload contracts roughly 2× into PostgreSQL.

## Ingest pipeline

A new endpoint, e.g. `POST /admin/object-explorer/releases`, accepts a multipart upload of one archive plus a label (`"BC 25.18"`). Processing runs synchronously inside the request (consistent with the rest of the toolbox — no background workers) and streams progress to the page via Blazor Server's interactive render.

1. **Stream the upload to a temp file.** A 500 MB body can't live in memory.
2. **Walk the archive's directory entries.** No full extraction. For each `.app` encountered:
   1. Skip if the path matches the test-filter list above.
   2. Stream-read the `.app` entry into a `MemoryStream`, strip the 40-byte `NAVX` prefix, treat the remainder as a ZIP.
   3. Parse `NavxManifest.xml`. Reject silently if `App@Id` or `App@Version` are missing (malformed `.app`).
   4. Idempotency check: if a `Module` with `(AppId, Version)` already exists in the target Release, **skip silently** (Decision 3).
   5. Parse `SymbolReference.json` into the typed structures. Walk the namespace tree depth-first, emitting one `ModuleObject` per codeunit / table / page / report / xmlport / query / controladdin / enum / interface / permissionset / pageextension / tableextension / reportextension / enumextension / permissionsetextension. Each object's `ReferenceSourceFileName` records the source path.
   6. Extract source: prefer `src/` inside the `.app` (when `IncludeSourceInSymbolFile="true"`); otherwise look for a paired `.Source.zip` in the same archive folder and use its `src/` tree. Store every `.al` text file in `ModuleFile`. Discard everything else.
   7. Emit `ModuleSymbol` rows (procedures, triggers, event publishers, event subscribers) by combining the symbol package's `Methods` array (public/internal — line numbers absent) with a source-side line-number lookup using the existing `AlSymbolExtractor`. Locals not in the symbol package are picked up by the same source extractor.
   8. Emit `ModuleVariable` rows for every object-scoped variable (globals only — procedure-locals are not in the symbol package and stay in the source-driven heuristic path).
   9. Emit `ModuleReference` rows for every fully-qualified type reference: variable subtypes, extension `TargetObject`, codeunit `TableNo` properties, report data items, event subscriber bindings (publisher event reference). Same-module refs whose Subtype omits `ModuleId` get stamped with the importing module's AppId.
3. **Two-pass resolution.** First pass: collect every Module's `(AppId, Name, Version)` and emit rows. Second pass — purely in-memory join — resolve each `ModuleReference.target_module_id` to the Module row imported in pass one. References that point at AppIds we *didn't* import are kept with a null `target_module_id` and a `target_app_id` so the UI can render "external — module not imported".
4. **Mark the Release ready.** Until the second pass completes, the Release is hidden from the version picker. Failure mid-ingest leaves a tombstone row (`status = 'failed'`, with an error message) that a SiteAdmin can clear.

Re-upload of a full Release with the same label: same idempotency rule — every `(AppId, Version)` already present is skipped. To replace a Release entirely the user has to delete it first.

## Entities

Naming is illustrative; final table names follow `domain-model.md` conventions (snake_case, explicit in `OnModelCreating`).

```
Organization ──┐
               └── has many ──> Release
                                  ├── (label, bc_version, imported_at, status)
                                  └── has many ──> Module (one per .app)
                                                     ├── (app_id, name, publisher, version, target, is_test, is_internal, is_language_pack, dependencies_json)
                                                     ├── has many ──> ModuleFile (one per .al file kept)
                                                     ├── has many ──> ModuleObject (codeunit/table/page/…)
                                                     │                   ├── (kind, object_id, name, source_file_id, line_number)
                                                     │                   ├── has many ──> ModuleSymbol (procedures, triggers, events, fields)
                                                     │                   └── has many ──> ModuleVariable (globals only; resolved type)
                                                     └── has many ──> ModuleReference
                                                                       ├── (source_object_id, target_module_id, target_object_kind, target_object_id, target_object_name, reference_kind)
                                                                       └── reference_kind ∈ { variable_type, extends_target, table_no, return_type, parameter_type, event_publisher, ... }
```

Two key design points:

- **`Release` replaces `BaseAppVersion`.** The label (`"BC 25.18"`) and BC platform/application versions come from any one of the contained Modules' manifests (Base App is canonical). The unit the user picks in the UI is a Release.
- **`Module.app_id` is the cross-Release identity.** Same AppId across two Releases lets us answer "how did `Codeunit 80 "Sales-Post"` evolve from BC 25.15 to 25.18" (Decision 5) without a separate evolution-tracking table.

A migration drops `base_app_versions` / `base_app_files` / `base_app_symbols` and any rows therein. The current single-version Object Explorer corpus is small and re-importable; the upgrade cost is "re-ingest your DVDs once".

## Reference resolution

Find references on a `ModuleObject` runs two queries in parallel and merges:

1. **Cross-module exact (SQL).** `SELECT … FROM module_references WHERE target_module_id = @id AND target_object_kind = @kind AND target_object_id = @objectId`. Every row is an authoritative hit; the UI groups by source module and reference kind. No regex, no false positives.
2. **Intra-module source scan.** The existing receiver-aware scanner (the band-aid plan) runs against the declaring module's source files. The global variable map is **read from `ModuleVariable`** instead of being regex-extracted — that's the upgrade. Procedure-local vars still come from the source-side regex. Results from this pass are split into the existing `Likely` / `PossiblyRelated` buckets.

The "Possibly related" bucket becomes much smaller because the heuristic only runs against one module's source, where the symbol package's globals already eliminate most receiver ambiguity.

Find references on a `ModuleSymbol` (procedure / field) is the same shape with one extra hop: the cross-module pass starts by finding every `ModuleVariable` whose subtype matches the object that *holds* the symbol, then narrows to call sites of the named member via source scanning of those holding modules. This is where the receiver-aware logic still earns its keep — but now bootstrapped from authoritative type data.

## UI

The Object Explorer's hierarchy becomes:

```
Object Explorer › BC 25.18 › Base Application › Agent
                  ↑              ↑                ↑
                  Release        Module           Object
```

A Release picker replaces the current version dropdown. A Module picker sits between Release and Object — defaults to "all modules" so the existing flat browse still works, but filters down once a module is chosen. The breadcrumb shows the full path. The file viewer's URL becomes `/object-explorer/{releaseId}/{moduleId}/files/{fileId}`.

Find-references results page shows two sections:

- **Cross-module references** (the new top section) — grouped by module, each module's hits grouped by source object. No "Possibly related" — every row is an exact reference.
- **Intra-module references** — the existing two-bucket display (Likely / Possibly related) scoped to the declaring module, fed by source scanning.

`_Exclude_`-flagged modules are styled the same as any other (Decision 4). A small badge ("internal" or similar) on the module chip is enough.

## Migration from the current shape

The current `base_app_*` tables and the `BaseAppImportService` source-ZIP path are dropped. Specifically:

- `base_app_versions`, `base_app_files`, `base_app_symbols`: dropped in the migration. EF entities removed.
- `BaseAppImportService`: replaced by a new `ReleaseImportService`. The minimal-API endpoint that fronted the source-ZIP upload is removed; the new endpoint replaces it.
- `BaseAppService.FindReferencesAsync`: rewritten to query against `ModuleReference` and use the new intra-module scanner. The receiver-aware band-aid plan's classifier (`ClassifyHitConfidence` and friends) stays — it's now reused for the intra-module fallback.
- `AlSymbolExtractor`, `AlGoToDefinitionLocator`: keep. The symbol-package path produces the same shape these emit; the source-side extractor is the fallback for everything the symbol package omits (locals, line numbers for the things that do have symbols but lack positions).
- `SymbolReindexer`, `SymbolReindexQueue`: keep but retarget at `Module` rows instead of `BaseAppFile`. Idempotent reindex is still useful for the source-side pass.

Forward-only migration. No data preservation — re-ingest your DVDs. There's no production traffic on this surface large enough to justify the migration work.

## Open questions

1. **Dependency graph in the UI.** Each `Module` carries `dependencies_json` from its manifest. Worth surfacing a "what does this module depend on / what depends on this module" view, or defer? Leaning defer until someone asks.
2. **Partial DVDs and out-of-order ingest.** If a user uploads just OIOUBL without Base App in the same Release, OIOUBL's `ModuleReference` rows point at a Base App ModuleId we don't have. Today's plan: store the rows with null `target_module_id`, render "external" in the UI. Upload Base App later into a *new* Release and OIOUBL's existing rows stay broken — they don't auto-link across Releases. Acceptable if the upload pattern is "one DVD = one Release"; rough if it's "build up a Release over time". Recommend: bake "one upload = one Release" into the model and don't try to support incremental Release composition.
3. **Storage growth across many Releases.** Two BC versions = two full DVDs of source. Per-Release deletion is supported, but no automatic cleanup. Worth a SiteAdmin pruning UI from day one.
4. **Symbol package vs source disagreements.** When a `.app`'s embedded source and its `SymbolReference.json` disagree on object IDs or names (e.g. the source was edited after compilation, shouldn't happen for Microsoft-shipped `.app`s but could happen for partner-shipped ones with mismatched artifacts), which wins? Recommend: symbol package wins, log a warning per discrepancy.

## Verification

The ingest pipeline gets test coverage in `ALDevToolbox.Tests/ObjectExplorer/`:

- `ReleaseImportServiceTests.IngestsDkCoreSampleEndToEnd` — fixtures: the DK Core `.app` and `.Source.zip` already inspected during this design. Assert: one Release row, one Module row with the right AppId/Version, four `ModuleObject` rows for the codeunits, three for the page extensions, seven for the report extensions; `ModuleVariable` rows for the report extensions' globals with resolved `(ModuleId=Base App AppId, Id=…)` subtype; no `_Exclude_`/test artifacts present.
- `ReleaseImportServiceTests.IngestsOIOUBLWithTableAndExtensions` — fixtures: OIOUBL `.app` plus separate source ZIP. Assert: the table's two fields exist as `ModuleSymbol` rows; tableextension `TargetObject` is captured as a `ModuleReference` row pointing at Base App's `Finance Charge Memo Line`; intra-module codeunit-to-codeunit reference (`OIOUBLDocumentEncode` var) resolves to the OIOUBL module itself.
- `ReleaseImportServiceTests.SkipsTestAndExcludeFiltersOnly` — synthetic archive with `Foo/Source/Microsoft_Foo.app`, `Foo/Test/Microsoft_Foo Tests.app`, `Foo/Source/Microsoft__Exclude_Internal.app`. Assert: Foo is ingested, Foo Tests is skipped, `_Exclude_Internal` is ingested with `IsInternal = true`.
- `ReleaseImportServiceTests.IgnoresDuplicateAppIdVersion` — same `.app` twice in two folders. Assert: one Module row, second occurrence silently skipped.
- `ReleaseImportServiceTests.HandlesPartnerAppWithSeparateSourceZip` — synthetic `.app` with `IncludeSourceInSymbolFile="false"`; assert source comes from the paired `.Source.zip`.
- `BaseAppServiceTests.FindReferences_returns_cross_module_exact_hits` — two-module fixture (a "consumer" module that declares `var X: Codeunit "Foo"; X.Bar()` plus a "library" module that declares `Codeunit "Foo" with procedure Bar`). Assert: the consumer's variable shows up as a cross-module reference with `reference_kind = variable_type`.

End-to-end smoke (manual, after CI green): import a real BC 25.18 DVD, open `Codeunit "Sales-Post"`, click Find references. Confirm hits from every Microsoft first-party extension that uses Sales-Post (Excel Reports, OIOUBL where applicable, EDocument connectors, etc.) appear in the cross-module section, with no false positives. Compare against the band-aid-only output of the current source-only Base App import to quantify precision lift.
