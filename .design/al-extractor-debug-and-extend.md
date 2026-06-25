# AL extractor — debug and extend guide

Companion to `.design/al-reference-extractor-refactor.md` (the design) and `.design/al-reference-extractor-gaps.md` (the known-issue punch list). This document is a maintainer's field guide for the post-refactor walker / resolver. Use it when an unresolved sample lands in the import log, when you're adding a new built-in to the allow-lists, or when you need to extend a per-kind structure extractor.

**Audience:** someone touching `Services/Al/*` or the AL bits of `Services/ObjectExplorer/*` for the first time. Read alongside the source files — every section names the relevant code paths so you can jump straight in.

**Status:** living doc. Update when the architecture moves; don't let it rot in parallel with the code.

## Map of the moving parts

Phase-2 reference extraction is split across a handful of files. Worth holding the whole shape in your head before you start poking:

```
Services/Al/
├── AlLexer.cs                       — token stream from raw .al source
├── AlSymbolExtractor.cs             — emits declarations + var_declaration positions
├── AlReferenceExtractor.cs          — Orchestrator + public DTOs
├── AlProcedureWalker.cs             — AlExtractionState + procedure-body grammar
├── AlBuiltinMethods.cs              — allow-lists (built-ins, system fns, DSL keywords)
├── AlGoToDefinitionLocator.cs       — click-time token inspection (used by the resolver)
└── Structure/
    ├── IAlObjectStructureExtractor.cs   — interface; three hooks
    ├── AlNullStructure.cs               — fallback (every kind we don't model)
    ├── AlTableStructure.cs              — field(N; "Name"; Type)
    ├── AlPageStructure.cs               — field("Ctl"; Rec.X) + part + layout + SubPageLink
    ├── AlQueryStructure.cs              — dataitem alias registration
    ├── AlReportStructure.cs             — dataitem alias registration
    ├── AlXmlportStructure.cs            — tableelement alias registration
    └── AlDataItemDsl.cs                 — shared helper for the alias-plus-source shape

Services/ObjectExplorer/
├── AppPackageReader.cs              — parses SymbolReference.json (strips namespaces from extends targets)
├── ReleaseImportService.cs          — orchestrates import + builds the resolver catalog
├── ReferenceResolver.cs             — shared resolver (extractor side + click-time side)
└── ReferenceSessionService.cs       — mints Find-references sessions, delegates to the resolver
```

### The orchestrator pattern

`AlReferenceExtractor.Orchestrator` is a thin token-dispatch loop. Per token it:

1. Skips file-level prologue (`namespace X.Y.Z;` / `using A.B.C;`).
2. Pushes a scope frame when it sees `procedure` / `trigger`, delegates body work to `AlProcedureWalker`.
3. At procedure-body scope, tries the procedural dispatches (member chain → typed literal → bare self-call → label use → implicit-Rec field access → global-variable use → field-receiver-context).
4. At object scope, gives the per-kind structure extractor first chance on the token, then falls through to shared property handlers (SourceTable, TableRelation, CalcFormula, …) and the generic DSL-keyword first-arg skip.
5. Handles top-level `[EventSubscriber(...)]` attributes.
6. Advances one token at the end if nothing claimed it.

`AlExtractionState` is the shared mutable state — token stream, position, refs list, scope stack, diagnostic counters, plus a couple of contextual slots (`CurrentFieldReceiver`, the OwnerType / RecType caches). Both the orchestrator and `AlProcedureWalker` mutate it; `AlProcedureWalker` is wired with a `_dispatchOneToken` callback so its `WalkBalancedParens` re-enters the orchestrator's main loop.

Per-kind structure extractors (`IAlObjectStructureExtractor`) get the same state plus the procedure walker. The interface has three hooks:

- `TryConsumeObjectScopeToken(tok)` — claims a kind-specific DSL keyword (`field(...)`, `part(...)`, `dataitem(...)`).
- `TryConsumeObjectScopeProperty(name)` — claims a kind-specific property (e.g. `SubPageLink` / `RunPageLink` on pages).
- `TryResolveObjectScopeBareIdentifier(tok)` — emits a `field_access` on a bare object-scope identifier when the extractor has a tracked dataitem / tableelement source.

Each hook has a default no-op so a new structure extractor only overrides what it owns.

### The resolver

`ReferenceResolver` is one class consulted by **both** the click-time path (`ReferenceSessionService.CreateAtPositionAsync`) and the extractor side (transitively via `IAlTypeResolver.ResolveMember`). Adding a new strategy goes in here and both paths benefit.

Production catalog: `CatalogResolver` (in `Services/ObjectExplorer/CatalogResolver.cs`, built by `ReleaseImportService` at import start) implements `IAlTypeResolver` against per-release dictionaries built once at import start:

- `_typesByName` — name → list of `AlTypeRef` candidates (multiple AppIds can share a name; resolver picks the visible one matching the kind hint).
- `_typesByObjectId` — DB id → AlTypeRef.
- `_objectIdByIdentity` — `(AppId, Kind, Name)` → DB id.
- `_members` — DB id → list of `MemberEntry`.
- `_extensionsByBaseName` — base name → list of `(AppId, ObjectId)` for visible extensions.
- `_sourceTablesByObjectId` — DB id → SourceTable name (for cross-page SubPageLink).
- `_visibleAppIds` — transitive dependency closure of the importing module.

When `ResolveMember` doesn't find a member on the owner's own members, it walks `_extensionsByBaseName[owner.Name]` and tries each visible extension. The member's `DeclaringType` is set to the extension's `AlTypeRef` so the emitted reference points at the extension, not the base — Find-references on the extension's procedure declaration row picks the call up.

### The snapshot harness

`ALDevToolbox.Tests/Al/Fixtures/<owner-kind>/<Name>.al` + paired `<Name>.snapshot.json` (and optional `<Name>.context.json`). Each fixture runs through `AlReferenceExtractor.Extract` and the harness asserts byte-equality against the committed snapshot. Missing snapshot → harness writes a baseline and fails loudly so you have to review + commit it.

Shared catalog: `SnapshotCatalog.InMemoryResolver` — register types via `AddType`, members via `AddMember`, page source-tables via `AddSourceTable`, tableextensions via `AddExtensionOf`. The in-memory resolver walks extensions exactly like the production `CatalogResolver` so fixtures meaningfully exercise the extension-membership path.

Run-once flow when adding a fixture:
1. Create `<Name>.al` and optionally `<Name>.context.json`.
2. Register any new types / members the fixture needs in `SnapshotCatalog`.
3. Run `dotnet test --filter "FullyQualifiedName~SnapshotTests"` — first run writes the baseline and fails.
4. Review the generated `<Name>.snapshot.json`. If it looks right, commit it.
5. Re-run — should pass.

## Debugging an unresolved sample

Phase-2 prints structured samples at end-of-import. Read one like:

```
Phase-2 unresolved sample: ReleaseId=2 Reason=chain-step Token='TransferFromAsmHeader'
  Line=37 Col=19 Owner=codeunit:Asm. Calculate BOM Tree
  ReceiverKind=table ReceiverName='BOM Buffer'
  ReceiverAppId=437dbf0e-84ff-417a-965d-ed2bb9650972
  Module=Base Application Path=src/.../AsmCalculateBOMTree.Codeunit.al
```

`Reason=` tells you which dispatch fired. Each one points at a different class of bug:

| Reason | What ran | Most likely root cause |
|---|---|---|
| `head-not-a-variable` | `TryConsumeMemberChain` couldn't resolve the chain head as a variable, parameter, or catalog type | Implicit-Rec field-as-chain-head (e.g. `"Document Type".AsInteger()` on a table) — see step 3 of the refactor. Otherwise: a missing scope-variable extraction (attribute-on-var-block, namespaced type reference, multi-line declaration). |
| `head-var-type-unresolved` | The head IS a variable, but its declared type isn't in the catalog | Visibility / dependency-graph gap, or the variable's type uses an unrecognised keyword. |
| `typed-literal-name` | `Kind::"Name"` shape where Name doesn't resolve under Kind | Namespace-qualified name not stripped, or a cross-release reference. |
| `chain-step` | Receiver resolved, but the member name isn't in the catalog (and isn't a built-in) | The method is on an extension whose base-name key doesn't match (namespace-qualified `ExtendsObjectName`), or local-procedure source extraction missed it, or it's on an app the visibility set doesn't include. |
| `bare-call` | Identifier-followed-by-`(` with no receiver doesn't match any built-in or owner member | Missing entry in `AlBuiltinMethods.BareCallableFunctions`, or the method is on the owner but our extraction dropped it. |

### Walking the data path

When you've got a `chain-step` or `head-var-type-unresolved` sample, the question is always **is the catalog wrong, or is the resolver wrong?** Run these in order:

1. **Confirm the receiver type the resolver picked.** `ReceiverAppId` in the log identifies the exact catalog row. If multiple modules ship same-named tables, this is the disambiguator.

   ```sql
   SELECT o.id, o.kind, o.name, m.app_id, m.name AS module_name
   FROM oe_module_objects o
   JOIN oe_modules m ON m.id = o.module_id
   WHERE o.name = '<ReceiverName>' AND o.kind = '<ReceiverKind>'
     AND m.app_id = '<ReceiverAppId>';
   ```

2. **List the member surface the resolver sees.** If the row from (1) has DB id `<owner_id>`, the catalog's "what does this owner expose?" answer is:

   ```sql
   SELECT s.kind, s.name FROM oe_module_symbols s
   WHERE s.object_id = <owner_id> ORDER BY s.name;
   ```

   If your missing token is **in this list**, the resolver isn't returning it for some reason — start tracing `CatalogResolver.ResolveMember` in `ReleaseImportService.cs`.

   If your missing token is **not in this list**, the catalog is the bug. Continue.

3. **Check for tableextension / pageextension declaration of the method.** Microsoft moves methods onto extensions more often than the .al source suggests:

   ```sql
   SELECT o.id, o.kind, o.name, o.extends_object_name, s.kind AS symbol_kind, s.name AS symbol_name
   FROM oe_module_objects o
   JOIN oe_module_symbols s ON s.object_id = o.id
   WHERE o.kind = 'tableextension' AND o.extends_object_name = '<ReceiverName>'
     AND s.name = '<MissingMember>';
   ```

   - **No row:** the method genuinely isn't catalogued. Either Microsoft's `.app` symbol package excludes it, or source extraction missed it (next step).
   - **Row exists but no reference:** `_extensionsByBaseName` key mismatch (namespace prefix on `extends_object_name`) or visibility issue. Look at `AppPackageReader.ParseExtendsRef` and `CatalogResolver.ResolveMember`'s extension-walk loop.

4. **Check whether source extraction is even running on the right file.** `EmitSymbols` in `ReleaseImportService.cs` only feeds extracted symbols into the per-object pipeline when the file's primary `object_declaration` matches `symObj.Name`:

   ```sql
   SELECT f.path, f.content_length FROM oe_module_files f
   JOIN oe_modules m ON m.id = f.module_id
   WHERE m.app_id = '<ReceiverAppId>'
     AND f.path LIKE '%<ReceiverName>%';
   ```

   If the file is loaded but the symbol isn't, look at the file content. Most common silent drops:
   - Procedure declared inside a `[...]` attribute that the regex didn't recognise — see "Common gotchas".
   - Multi-name var declaration (`A, B: Integer;`) — `VarDeclarationRegex` doesn't currently match these.
   - The procedure is on a file whose first `object_declaration` name didn't match the symbol-package's `symObj.Name` (multi-object files; rare in BC).

### Reproducing in the snapshot harness

The fastest way to confirm a fix works without re-importing a 90-second BC release is to mirror the case in a fixture under `ALDevToolbox.Tests/Al/Fixtures/`. The catalog you build via `SnapshotCatalog.InMemoryResolver` exercises the same `IAlTypeResolver` surface as production, including the extension walk.

Pattern:
1. Add the receiver type + members to `SnapshotCatalog` (use `AddExtensionOf` for tableextension cases).
2. Write a small `.al` that exercises the exact construct.
3. Run the snapshot test once to write the baseline.
4. Inspect the JSON — does it have the references you want?
5. If not, hack the production extractor; iterate.

The harness round-trip is sub-second; the BC re-import is 90 seconds. Use the harness.

## Extending: cookbook

### Add a system function that's callable without a type prefix

E.g. Microsoft ships a new `WhateverNew()` system function. Today it triggers `bare-call` unresolved samples because the bare-self-call dispatch doesn't recognise it.

**Edit:** `Services/Al/AlBuiltinMethods.cs`, `BareCallableFunctions`.

```csharp
public static readonly HashSet<string> BareCallableFunctions = new(StringComparer.OrdinalIgnoreCase)
{
    // ... existing entries ...
    "WhateverNew",
};
```

**When NOT to add here:** if the method is already in a receiver-method set (`RecordMethods`, `PageMethods`, `QueryMethods`, `TextMethods`, `CommonMethods`, …), `TryConsumeBareSelfCall`'s fallthrough already catches it. Adding to `BareCallableFunctions` would short-circuit the owner-member resolver — if a user has a procedure named the same way (`procedure WhateverNew()`), its `method_call` references would be lost. The bare-callable list is for system functions that are NEVER also user procedures: clear naming + low collision risk.

`.design/al-methodswithoutdatatype.md` has the upstream-documented list of 229 names. Anything in that list that isn't already in `BareCallableFunctions` + the receiver-method sets is a fair add.

### Add a record built-in that takes field names as args

E.g. `Rec.NewMethodTakingField("Some Field", value)`.

**Edit:** `Services/Al/AlBuiltinMethods.cs`, `FieldNameTakingMethods`.

```csharp
public static readonly HashSet<string> FieldNameTakingMethods = new(StringComparer.OrdinalIgnoreCase)
{
    // ... existing ...
    "NewMethodTakingField",
};
```

The chain walker's `WalkArgsForBuiltin` checks this set on every method-call dispatch; when the name's in there, it sets `AlExtractionState.CurrentFieldReceiver` for the duration of the parens and the bare-arg dispatch hook (`TryResolveFieldReceiverContext`) emits `field_access` against it. Nested calls properly save / restore.

### Add a new bare-call diagnostic-silencing for a DSL keyword

E.g. AL adds a new layout container `mywidget(...)` on pages. Today it'd fire `bare-call` because no `mywidget` handler exists.

**Edit:** `Services/Al/AlBuiltinMethods.cs`, `ObjectDslKeywords`.

```csharp
public static readonly HashSet<string> ObjectDslKeywords = new(StringComparer.OrdinalIgnoreCase)
{
    // ... existing ...
    "mywidget",
};
```

The generic DSL-keyword first-arg skip fires for any identifier in this set at object scope followed by `(`. The first arg gets skipped, the rest of the parens walks through the main dispatch (so an inner `Rec."Field Name"` source expression still emits its reference).

**When you want more than a skip:** for keywords like `dataitem` / `tableelement` where the first arg is an alias you want to register, the per-kind structure extractor is the right home. See "Add a new per-kind structure extractor".

### Add a new property handler

E.g. a new page property `MyShinyProperty = field("X");` where `"X"` should resolve against Rec.

If the property handles like one we already have (single object reference / TableRelation-style / where()-sorting() / SubPageLink-style), add a case in `Orchestrator.TryConsumeObjectScopeProperty` and route to the existing handler. For property values that don't fit any of those, write a new private `TryConsume<Name>` method following the existing pattern: advance past name + `=`, walk the value tokens emitting refs against the right receiver, advance past `;`.

When the property is kind-specific (only meaningful on pages, only meaningful on tables), put it on the per-kind structure extractor's `TryConsumeObjectScopeProperty(propertyName)` instead. The orchestrator gives the per-kind hook first chance, then falls through to its shared dispatch.

### Add a new per-kind structure extractor

E.g. we want a dedicated extractor for `controladdin` because new controladdins start emitting kind-specific DSL we don't currently model.

1. Create `Services/Al/Structure/AlControlAddInStructure.cs`:

   ```csharp
   internal sealed class AlControlAddInStructure : IAlObjectStructureExtractor
   {
       private readonly AlExtractionState _state;
       public AlControlAddInStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
       {
           _state = state;
           _ = procedureWalker;  // accept-but-unused matches sibling extractors
       }

       public bool TryConsumeObjectScopeToken(AlToken tok)
       {
           // Recognise your kind-specific DSL keyword shape, advance Pos
           // past it, return true to claim. Return false to fall through.
           return false;
       }
   }
   ```

2. Register the kind in `AlReferenceExtractor.Orchestrator.SelectStructure`:

   ```csharp
   "controladdin" => new AlControlAddInStructure(state, procedureWalker),
   ```

3. Add fixtures under `ALDevToolbox.Tests/Al/Fixtures/controladdin/`. Run the snapshot test once to capture baselines.

**Don't inherit from a base class.** Per `.design/al-reference-extractor-refactor.md`: composition over `AlProcedureWalker` reads better than a base class with virtuals. If two extractors share logic (like `AlQueryStructure` and `AlReportStructure` both doing `dataitem(Alias; Source)`), factor the shared helper into a static like `AlDataItemDsl.TryConsumeAliasedSourceDeclaration` and call from each.

### Add a new resolution strategy

E.g. a new "clicked token resolves to an enum value" affordance.

1. Add a new variant to `ResolutionTarget` (in `Services/ObjectExplorer/ReferenceResolver.cs`):

   ```csharp
   public sealed record EnumValueTarget(long EnumObjectId, string ValueName) : ResolutionTarget;
   ```

2. Add a private strategy method to `ReferenceResolver`:

   ```csharp
   private async Task<DbMatch?> TryResolveEnumValueAsync(ResolverContext ctx, CancellationToken ct)
   {
       // ... query oe_module_objects + symbols for an enum value matching the click ...
   }
   ```

3. Add the strategy to the chain in `ReferenceResolver.ResolveAsync`, between existing strategies. Reason tag should be a short kebab-case string (`enum-value`).

4. Add a switch arm in `ReferenceSessionService.CreateAtPositionAsync` that mints a session for the new target.

### Add a new reference kind (e.g. `variable_use`-style)

The extractor emits to `ExtractedReference.ReferenceKind`; the import side persists to `oe_module_references.reference_kind`. To add a new kind:

1. Decide on the storage shape — does it need a new column on `ModuleReference`? Variable uses needed `TargetVariableId`. New kinds with a unique target shape need a new nullable FK column and a migration with a filtered index for the find-references join.
2. Emit from the extractor — `_state.Refs.Add(new ExtractedReference(... ReferenceKind: "your_kind", ...))`.
3. Stamp the new column at import time — `EmitCallSiteReferencesAsync` in `ReleaseImportService.cs` is where targetSymbolId / targetVariableId stamping lives; add a parallel lookup.
4. Update `ReferenceSessionService.CreateFromXxxAsync` (or add a new one) for the click-time path.

## Common gotchas

These are the surprise patterns that have bitten us. Read once, save yourself an hour later.

### Namespaced extends targets

Microsoft's modern BC symbol packages emit `TargetObject = "#<appid>#Microsoft.Inventory.BOM.BOM Buffer"` even when the `.al` source writes `extends "BOM Buffer"`. `AppPackageReader.ParseExtendsRef` strips the namespace prefix; if you're touching that code, keep the strip. The catalog stores tables under unqualified names; ANY consumer that joins back to the base object's name has to compare unqualified.

Same risk shape applies to typed-literal references (`Database::Microsoft.X.Y."Some Table"`) and var-block type references (`Record Microsoft.X.Y."Some Table"`) — `TryConsumeTypedLiteralChain` and `ReadTypeReference` both walk the dotted prefix and use the last segment.

### Variable-block attributes silently consume the next declaration

`var [SecurityFiltering(SecurityFilter::Filtered)] X: Record "Y";` used to drop `X` from scope entirely — the var-block parser advanced past `[`, read `SecurityFiltering` as the variable name, the `:` check failed, and `SkipToSemicolonOrBegin` ate everything up to and including the actual declaration. `AlExtractionState.SkipAttribute` handles this now in `ParseVarBlock` AND `ParseParameterList`. If you add another var-block-ish parser, remember to skip attributes first.

### Tableextension's extends target name keys the resolver's extension walk

`_extensionsByBaseName` is keyed by `oe_module_objects.extends_object_name`. The resolver looks up extensions of an owner via `owner.Name`. Both must agree — namespace stripping at parse time keeps them consistent. If a future Microsoft version emits a new encoding (different separator, alias path), `ParseExtendsRef`'s strip has to evolve.

### Dataitem-alias context is most-recent-wins, not stacked

`AlQueryStructure._currentDataItemSource` (same on `AlReportStructure` and `AlXmlportStructure`) tracks the most-recent `dataitem(...; SourceTable)`. Inside a nested dataitem, the inner overwrites the outer. AL grammar in practice puts a dataitem's columns / filters inside its own `{ }` before the next dataitem begins, so the slot suffices. If you find a case where a column declared AFTER a nested dataitem closed misresolves against the inner, this is why — a stack would be the proper fix.

### Field-receiver context for built-in args has nesting semantics, NOT scope semantics

`AlExtractionState.CurrentFieldReceiver` is saved+restored at each `WalkArgsForBuiltin` boundary. Nested calls (`OuterRec.Validate(InnerRec.FieldNo("X"), 0)`) correctly resolve `"X"` against `InnerRec`. But there's no "first arg only" tracking — all bare-id args inside `Validate(...)` get tested against the receiver. False positives are silenced by the catalog miss (a non-field bare-id won't accidentally resolve as a field). If you add a built-in whose args are ambiguous, document why it's in `FieldNameTakingMethods`.

### Implicit-Rec only fires inside a procedure body

`TryConsumeImplicitFieldAccess` and `TryConsumeImplicitRecFieldChainHead` both gate on `ScopeStack.Count > 1`. At object scope (in property values, in `TableRelation`, in CalcFormula), bare-field-on-Rec lookups go through dedicated handlers — `EmitFieldsInsideClause` / `DataCaptionFields` / `TryEmitBareFieldOnSource` (per-kind structure hook). If you add a new object-scope construct that should resolve bare field names against a context, route through `TryResolveObjectScopeBareIdentifier` on the per-kind extractor.

### Symbol-package methods exclude `local procedure`s

The symbol package only ships public + internal methods (`SymbolMethod.IsInternal` distinguishes). Locals come from source extraction via the `EmitSymbols` fallback loop, which adds extracted procedures not consumed by the package loop. If a method should be in the catalog but isn't, check whether (a) Microsoft marked it `local`, (b) the source file is loaded, (c) the file's primary `object_declaration` matches `symObj.Name` so source feeding kicks in.

### Re-import is required for catalog-shape changes

Migrations of `oe_module_symbols` / `oe_module_references` / `oe_module_variables` only fix newly-imported releases. After deploying a kind-vocabulary change, an extends-name-strip fix, or anything that touches the import pipeline's emit logic, releases imported before the change still hold the old data. Soft-deleted releases stay invisible; for active ones the user has to re-import.

## Telemetry conventions

`UnresolvedSample.Reason` is the operator-facing grep key. The samples appear in the import log as one line per sample, capped at 50 per import. Per-kind extractors that add new diagnostics should follow these prefix conventions so a `grep -E 'Reason=(query|report|xmlport):'` style filter works:

| Prefix | Owner |
|---|---|
| `head-not-a-variable` / `head-var-type-unresolved` | `AlProcedureWalker` chain head |
| `typed-literal-name` | `AlProcedureWalker` typed-literal chain |
| `chain-step` | `AlProcedureWalker` member chain |
| `bare-call` | `AlProcedureWalker` bare self-call |
| `page:…` (planned) | `AlPageStructure` |
| `table:…` (planned) | `AlTableStructure` |
| `report:…` (planned) | `AlReportStructure` |
| `query:…` (planned) | `AlQueryStructure` |
| `xmlport:…` (planned) | `AlXmlportStructure` |
| `proc:…` (planned) | `AlProcedureWalker` future granular |

`ReceiverAppId` accompanies every chain-step / head-var-type-unresolved sample. Use it as the canonical disambiguator when the same name spans modules.

## Verification checklist for a refactor commit

Whenever you touch `Services/Al/*` or `Services/ObjectExplorer/ReleaseImportService.cs`'s extractor / resolver bits:

1. `dotnet build` is clean.
2. `dotnet test --filter "FullyQualifiedName~Tests.Al"` green.
3. `dotnet test --filter "FullyQualifiedName~SnapshotTests"` green. If a snapshot intentionally changed, regenerate it in the same commit (delete the `.snapshot.json`, re-run, review the diff, commit). Never let a snapshot change drift across commits.
4. For DB-side changes (entity schema, catalog construction, import pass), spot-check via the full local Postgres run-through (`dotnet test` against `ALDevToolbox.Tests/ObjectExplorer/ReleaseImportServiceTests.cs`).
5. For changes that ripple into the BC import: do an end-to-end re-import of one BC release on the local dev DB. Compare resolved + unresolved counts to the previous baseline. Sample log for new `Reason=` buckets you didn't expect.

## Pointers when you're stuck

- The design rationale lives in `.design/al-reference-extractor-refactor.md`. Read it when you're unsure why two pieces are split the way they are.
- Known gaps that aren't bugs (`with X do …`, `#if` predicates, Variant tracking) are in `.design/al-reference-extractor-gaps.md`. Don't chase them unless you've decided to close one.
- Microsoft's "AL methods invokable without the data type name" catalogue: `.design/al-methodswithoutdatatype.md`. Cross-reference when adding to `BareCallableFunctions`.
- BC's open-source Base App on github.com/StefanMaron/MSDyn365BC.Code.History is the canonical reference for what shapes Microsoft actually ships. If you suspect a parsing bug, find a real BC file that hits the pattern.
- The CLAUDE.md "Stay inside the architectural fences" section lists hard constraints. The walker / resolver lives inside those — don't break them when refactoring.
