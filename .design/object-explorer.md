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
   7. Emit `ModuleSymbol` rows (procedures, triggers, event publishers, event subscribers) from the symbol package's `Methods` array. The shipped implementation leaves `ModuleSymbol.LineNumber` / `ColumnStart` / `ColumnEnd` at 0 — symbol-package coordinates are not stored statically; consumers that need them re-derive against the file content. `ModuleObject.LineNumber` is stamped during ingest via a single regex pass over each `.al` file. Locals not in the symbol package are out of scope for the current implementation; a future intra-procedure scanner can fill them in incrementally.
   8. Emit `ModuleVariable` rows for every object-scoped variable (globals only — procedure-locals are not in the symbol package and stay in the source-driven heuristic path).
   9. Emit `ModuleReference` rows for every fully-qualified type reference: variable subtypes, extension `TargetObject`, codeunit `TableNo` properties, report data items, event subscriber bindings (publisher event reference). Same-module refs whose Subtype omits `ModuleId` get stamped with the importing module's AppId. Reference rows store the `target_app_id` triplet from the symbol package; they do **not** carry a resolved `target_module_id` — that's computed at query time via the parent-chain join above.
3. **Mark the Release ready.** Once every `.app` in the archive has been processed, flip the Release row's `status` from `'ingesting'` to `'ready'`. Until then, the Release is hidden from the picker. Failure mid-ingest leaves a tombstone row (`status = 'failed'`, with an error message) that a SiteAdmin can clear.

Re-upload of a full Release with the same label: same idempotency rule — every `(AppId, Version)` already present is skipped. To replace a Release entirely the user has to delete it first.

## Entities

The current `base_app_*` tables are dropped. Their replacements drop the "base app" naming entirely because the new tables hold any `.app` content, not just Microsoft's Base Application. SQL table names take an `oe_` prefix to keep them clearly Object-Explorer-owned and to avoid colliding with the unrelated template-catalogue `Module` entity (`Domain/Entities/Module.cs`, used in `unified-extensions.md`). C# entity types live in a new `Domain/Entities/ObjectExplorer/` folder so the type `ALDevToolbox.Domain.Entities.ObjectExplorer.Module` is distinct from the existing `ALDevToolbox.Domain.Entities.Module`.

```
Organization ──┐
               └── has many ──> Release  (table: oe_releases)
                                  ├── (label, bc_version, kind, parent_release_id, imported_at, status)
                                  └── has many ──> Module  (table: oe_modules, one per .app)
                                                     ├── (app_id, name, publisher, version, target,
                                                     │    is_test, is_internal, is_language_pack,
                                                     │    dependencies_json)
                                                     ├── has many ──> ModuleFile     (oe_module_files, one per .al kept)
                                                     ├── has many ──> ModuleObject   (oe_module_objects)
                                                     │                   ├── (kind, object_id, name,
                                                     │                   │    source_file_id, line_number)
                                                     │                   ├── has many ──> ModuleSymbol   (oe_module_symbols — procedures, triggers, events, fields)
                                                     │                   └── has many ──> ModuleVariable (oe_module_variables — globals only; resolved type)
                                                     └── has many ──> ModuleReference (oe_module_references)
                                                                       ├── (source_object_id,
                                                                       │    target_app_id, target_object_kind,
                                                                       │    target_object_id, target_object_name,
                                                                       │    reference_kind)
                                                                       └── reference_kind ∈ { variable_type, extends_target,
                                                                                              table_no, return_type, parameter_type,
                                                                                              event_publisher, ... }
```

Service rename landed in step with the schema: `BaseAppService` → `ObjectExplorerService`. The find-references query mechanics live in the same class; a sibling `ReferenceQueryService` may earn its own file when the implementation grows another set of resolution kinds.

Four key design points:

- **`Release` replaces `BaseAppVersion`.** The label (`"BC 25.18"`) and BC platform/application versions come from any one of the contained Modules' manifests (Base App is canonical when present). The unit the user picks in the UI is a Release.
- **`Module.app_id` is the cross-Release identity.** Same AppId across two Releases lets us answer "how did `Codeunit 80 "Sales-Post"` evolve from BC 25.15 to 25.18" (Decision 5) without a separate evolution-tracking table.
- **`Release.parent_release_id` enables layered third-party uploads.** A first-party DVD has `parent_release_id = NULL` and `kind = 'first_party'`. A third-party Release (a partner app, a customer's customisation set) has `parent_release_id` pointing at another Release and `kind = 'third_party'` (or `'customer'` — single short enum, final values decided when the third-party UI lands). Chains are arbitrary depth — Customer X → Continia Document Capture → Continia Core → Base Application is a real four-layer stack that the schema must support, since partner apps routinely depend on each other before bottoming out on Base App. Reference resolution walks the chain via a PostgreSQL recursive CTE; resolver code is depth-agnostic.
- **Reference targets are stored as facts, not as resolved pointers.** `oe_module_references` carries the `(target_app_id, target_object_kind, target_object_id, target_object_name)` triplet exactly as the symbol package reports it. The actual target `Module` row is **resolved at query time** by joining against the parent-release chain. This makes retargeting a one-line `UPDATE oe_releases SET parent_release_id = …` instead of a bulk row rewrite, and it lets the same reference row resolve to *different* modules depending on which Release chain it's queried against — which is exactly what powers conflict detection on retarget (see "Retargeting and upgrade-conflict detection" below).

A migration drops `base_app_versions` / `base_app_files` / `base_app_symbols` and any rows therein. The current single-version Object Explorer corpus is small and re-importable; the upgrade cost is "re-ingest your DVDs once".

## Reference resolution

Find references on a `ModuleObject` runs two queries in parallel and merges:

1. **Cross-module exact (SQL).** The resolution is a JOIN of `oe_module_references` against `oe_modules`, scoped by a recursive CTE over `parent_release_id` so the chain of Releases containing the current browse context plus its ancestors and descendants is in play. Outline:

   ```sql
   WITH RECURSIVE chain(release_id) AS (
       SELECT id FROM oe_releases WHERE id = @currentRelease
       UNION ALL
       SELECT r.parent_release_id FROM oe_releases r
       JOIN chain c ON r.id = c.release_id WHERE r.parent_release_id IS NOT NULL
       UNION ALL
       SELECT r.id FROM oe_releases r
       JOIN chain c ON r.parent_release_id = c.release_id
   )
   SELECT mr.*, src.module_id AS src_module_id, src_m.name AS src_module_name
   FROM oe_module_references mr
   JOIN oe_module_objects src ON src.id = mr.source_object_id
   JOIN oe_modules src_m ON src_m.id = src.module_id
   JOIN oe_modules tgt_m ON tgt_m.app_id = mr.target_app_id
                        AND tgt_m.release_id IN (SELECT release_id FROM chain)
   WHERE tgt_m.id = @targetModuleId
     AND mr.target_object_kind = @kind
     AND mr.target_object_id   = @objectId;
   ```

   The recursive CTE walks both directions: ancestors (so a third-party Release's references *out* into Base App surface up) and descendants (so a first-party Release sees third-party hits *in*). Every row is an authoritative hit; the UI groups by source module and reference kind. No regex, no false positives. An index on `oe_module_references (target_app_id, target_object_kind, target_object_id)` keeps the join cheap.

   **Same-AppId shadowing.** When the chain contains more than one Module with the same `app_id` at different versions (legal — idempotency only deduplicates exact `(AppId, Version)` matches, so a child Release can legitimately pull in a newer or older copy of an ancestor's app), the resolver picks the one **closest to the current Release** in the chain. The CTE assigns a `chain_depth` to each step; the join's `ORDER BY chain_depth ASC LIMIT 1` per `(target_app_id, target_object_kind, target_object_id)` triplet picks the winner. The shadowed Module is still reachable when its declaring Release is browsed directly — shadowing is per-query-context, not a permanent hide.
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

### Upload flow for third-party Releases

When the third-party upload UI lands (deferred milestone — schema ships earlier), the form is hybrid: it reads the uploaded `.app`'s `NavxManifest.xml`, pulls the `Dependencies` block plus the `Application` version constraint, and looks for existing Releases that satisfy them. The form's "Parent Release" dropdown defaults to the inferred match (annotated "(inferred from manifest)") with the full list of existing Releases available as an override. Multiple satisfying Releases — common when several BC versions are imported — surface the closest version match by default; the user can pick a different one. No automatic match available (no Release satisfies the dependencies) means the user must pick explicitly or the upload is refused.

### Retargeting and upgrade-conflict detection

A third-party Release's parent is mutable. `Releases › <release> › Settings` exposes a "Retarget to a different Release" action that runs `UPDATE oe_releases SET parent_release_id = @newParent WHERE id = @release`. Because reference rows store target facts (AppId + ObjectKind + ObjectId + ObjectName) rather than resolved Module pointers, every subsequent find-references query re-resolves transparently against the new parent chain. No row rewriting, no re-ingest.

This makes upgrade-conflict detection a derived feature, no separate machinery needed. After retargeting Customer X from BC 25.18 to BC 25.20, the same query that powers Find references also answers "what *broke*":

```sql
-- References on a retargeted Release whose target no longer resolves
SELECT mr.target_app_id, mr.target_object_kind, mr.target_object_name, COUNT(*) AS broken_count
FROM oe_module_references mr
JOIN oe_module_objects o ON o.id = mr.source_object_id
JOIN oe_modules m ON m.id = o.module_id
WHERE m.release_id = @retargetedRelease
  AND NOT EXISTS (
      SELECT 1 FROM oe_modules tgt
      WHERE tgt.app_id = mr.target_app_id
        AND tgt.release_id IN (SELECT release_id FROM chain_for(@retargetedRelease))
        AND EXISTS (SELECT 1 FROM oe_module_objects to_
                    WHERE to_.module_id = tgt.id
                      AND to_.kind = mr.target_object_kind
                      AND (to_.object_id = mr.target_object_id OR to_.name = mr.target_object_name))
  )
GROUP BY mr.target_app_id, mr.target_object_kind, mr.target_object_name
ORDER BY broken_count DESC;
```

The result is exactly an upgrade compatibility report: "Codeunit X went away", "Field Y was renamed", "Procedure Z's signature changed and its overload no longer matches". The UI for this is a `Releases › <release> › Compatibility` page that runs the above against the current parent chain, grouped by target module. Empty result = the retarget is a clean lift. Non-empty result = work to do.

Two design notes on retargeting:

- Allow retarget only on Releases with `kind ∈ {'third_party', 'customer'}`. First-party Releases have `parent_release_id = NULL` by definition; the column stays null. The form refuses to expose retargeting on first-party rows.
- Retargeting doesn't break the symmetric resolution from the parent's side. After Customer X moves from BC 25.18 to BC 25.20, BC 25.18 stops seeing Customer X's calls *into* it (because the chain no longer joins them) and BC 25.20 starts seeing them. Both directions are correct under the new chain.

## Migration from the current shape

The current `base_app_*` tables and the `BaseAppImportService` source-ZIP path are dropped. Specifically:

- `base_app_versions`, `base_app_extensions`, `base_app_files`, `base_app_symbols`: dropped in `20260602000000_DropBaseAppTables`. EF entities removed in the same PR. The new tables (`oe_releases`, `oe_modules`, `oe_module_files`, `oe_module_objects`, `oe_module_symbols`, `oe_module_variables`, `oe_module_references`) were created earlier in `20260601000000_AddObjectExplorerTables`.
- `BaseAppImportService` is replaced by `ReleaseImportService`. The minimal-API endpoint that fronted the source-ZIP upload is removed; the new `POST /admin/object-explorer/import` replaces it.
- `BaseAppService` is replaced by `ObjectExplorerService`. `FindReferencesAsync` runs against `oe_module_references` joined through a recursive-CTE chain walk; cross-module references are resolved by stored `target_app_id` triplets at write time rather than re-resolved at query time. The receiver-aware band-aid classifier (`ClassifyHitConfidence` and friends) is retired entirely — variable subtypes are exact in the symbol package, so the heuristics it papered over no longer have anything to disambiguate.
- `AlSymbolExtractor`, `AlGoToDefinitionLocator`, `AlDeclarationParser`, `AlResolvableTokenScanner`: removed along with the legacy file viewer / inspector / go-to-definition surface. The shipped Object Explorer relies on the symbol package's own coordinates plus a single regex pass at ingest time for object-header line numbers; intra-procedure navigation is not (yet) re-implemented for the new surface.
- `SymbolReindexer`, `SymbolReindexQueue`: removed. Re-ingest happens by re-uploading the DVD; idempotency on `(release_id, app_id, version, file_hash)` makes that cheap.
- Razor pages and URL routes under `Components/Pages/ObjectExplorer/` shift from `{versionId}` to `{releaseId}` / `{moduleId}` / `{objectId}` segments. Old links break — there are no production redirects to preserve.

Forward-only migration. No data preservation — re-ingest your DVDs. There's no production traffic on this surface large enough to justify the migration work.

## Open questions

1. **Dependency graph in the UI.** Each `Module` carries `dependencies_json` from its manifest. Worth surfacing a "what does this module depend on / what depends on this module" view, or defer? Leaning defer until someone asks.
2. **Storage growth across many Releases.** Two BC versions = two full DVDs of source. Per-Release deletion is supported, but no automatic cleanup. Worth a SiteAdmin pruning UI from day one. Deletion of a parent Release should be refused while any child Release still points at it (PostgreSQL FK with `ON DELETE RESTRICT`).
3. **Symbol package vs source disagreements.** When a `.app`'s embedded source and its `SymbolReference.json` disagree on object IDs or names (shouldn't happen for Microsoft-shipped `.app`s, could happen for partner-shipped ones with mismatched artifacts), which wins? Recommend: symbol package wins, log a warning per discrepancy.
4. **Conflict-report fidelity.** The retarget conflict query above flags references whose `(target_app_id, target_object_kind, (target_object_id OR target_object_name))` no longer matches anything in the new chain. That catches object removals and renames but not deeper semantic breaks — a procedure that still exists with the same name but whose parameter list changed will not flag here. Worth picking up signature mismatches in a follow-up: the symbol package's `Methods[].Parameters` already gives us typed signatures per Release, so a procedure-signature-diff pass across two Releases of the same AppId is feasible but adds complexity. Defer until someone hits the case.

## Verification

The ingest pipeline gets test coverage in `ALDevToolbox.Tests/ObjectExplorer/`:

- `ReleaseImportServiceTests.IngestsDkCoreSampleEndToEnd` — fixtures: the DK Core `.app` and `.Source.zip` already inspected during this design. Assert: one Release row, one Module row with the right AppId/Version, four `ModuleObject` rows for the codeunits, three for the page extensions, seven for the report extensions; `ModuleVariable` rows for the report extensions' globals with resolved `(ModuleId=Base App AppId, Id=…)` subtype; no `_Exclude_`/test artifacts present.
- `ReleaseImportServiceTests.IngestsOIOUBLWithTableAndExtensions` — fixtures: OIOUBL `.app` plus separate source ZIP. Assert: the table's two fields exist as `ModuleSymbol` rows; tableextension `TargetObject` is captured as a `ModuleReference` row pointing at Base App's `Finance Charge Memo Line`; intra-module codeunit-to-codeunit reference (`OIOUBLDocumentEncode` var) resolves to the OIOUBL module itself.
- `ReleaseImportServiceTests.SkipsTestAndExcludeFiltersOnly` — synthetic archive with `Foo/Source/Microsoft_Foo.app`, `Foo/Test/Microsoft_Foo Tests.app`, `Foo/Source/Microsoft__Exclude_Internal.app`. Assert: Foo is ingested, Foo Tests is skipped, `_Exclude_Internal` is ingested with `IsInternal = true`.
- `ReleaseImportServiceTests.IgnoresDuplicateAppIdVersion` — same `.app` twice in two folders. Assert: one Module row, second occurrence silently skipped.
- `ReleaseImportServiceTests.HandlesPartnerAppWithSeparateSourceZip` — synthetic `.app` with `IncludeSourceInSymbolFile="false"`; assert source comes from the paired `.Source.zip`.
- `ObjectExplorerServiceTests.FindReferences_returns_cross_module_exact_hits` — two-module single-Release fixture (a "consumer" module that declares `var X: Codeunit "Foo"; X.Bar()` plus a "library" module that declares `Codeunit "Foo" with procedure Bar`). Assert: the consumer's variable shows up as a cross-module reference with `reference_kind = variable_type`.
- `ObjectExplorerServiceTests.FindReferences_resolves_across_parent_release` — two-Release fixture: parent first-party Release holds the library module declaring `Codeunit "Foo"`; child third-party Release holds the consumer module. Browsing the consumer's `Bar` procedure, assert the parent module shows up; browsing the library's `Bar` from the parent Release with the child attached, assert the child's call site shows up. Then assert that browsing the parent Release **without** the child attached doesn't surface the child's hits (releases are isolated unless linked by parent).
- `ObjectExplorerServiceTests.FindReferences_resolves_through_multi_layer_chain` — three-Release fixture mirroring a real BC stack: layer 0 = "Base App" with `Codeunit "Foo"`; layer 1 = "Continia Core" (third-party) on top of layer 0, declaring `Codeunit "Cont Helper"` that calls Foo; layer 2 = "Continia Document Capture" (third-party) on top of layer 1, declaring a codeunit that calls Cont Helper. Assert: from layer 2, references to `Foo` resolve up through layer 0 (skipping nothing); from layer 0, browsing `Foo` shows the call site in layer 1 *and* the indirect reference is **not** doubled up from layer 2.
- `ObjectExplorerServiceTests.Retarget_updates_resolution_without_rowrewrite` — start with a third-party Release whose `parent_release_id` points at BC 25.18. A reference into Base App resolves to that Release's `Codeunit "Foo"`. Update `parent_release_id` to a fixture "BC 25.20" Release (also containing `Codeunit "Foo"` but at a different `Module.id`). Same find-references query now returns the 25.20 Module without touching `oe_module_references` rows. Assert `oe_module_references` rows are byte-identical before and after.
- `ObjectExplorerServiceTests.Retarget_surfaces_broken_references_in_compatibility_report` — retarget a third-party Release from a parent that has `Codeunit "Foo"` to a parent that has removed it (or renamed it). The compatibility-report query returns one broken-reference row keyed on the removed object; the same retarget back resolves to zero broken rows. Validates the "upgrade conflict detector" feature falls out of the resolution model without additional code.

End-to-end smoke (manual, after CI green): import a real BC 25.18 DVD, open `Codeunit "Sales-Post"`, click Find references. Confirm hits from every Microsoft first-party extension that uses Sales-Post (Excel Reports, OIOUBL where applicable, EDocument connectors, etc.) appear in the cross-module section, with no false positives. Compare against the band-aid-only output of the current source-only Base App import to quantify precision lift.
