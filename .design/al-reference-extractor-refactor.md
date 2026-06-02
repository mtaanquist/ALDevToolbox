# AL reference extractor — structural refactor

This document captures the design lessons from the iterative noise-reduction work on `AlReferenceExtractor` (PR #159) and proposes a structural refactor that addresses the root causes those band-aids worked around. It's a follow-up doc, not a milestone replacement: the existing extractor is good enough for the current v0.5 alpha and will merge to `main` as-is; this work goes on a fresh branch where destructive changes are acceptable since the toolbox isn't in production yet.

**Status:** proposal. Written after the noise-reduction PR shipped, before any of the refactor work has started. A planning agent or implementer should treat this as a punch list to scope against `.design/object-explorer.md` (the canonical Object Explorer spec) and `.design/al-reference-extractor-gaps.md` (the existing gaps list — extends it rather than replaces it).

## Why this exists

The current `AlReferenceExtractor` works. It resolves the bulk of references across a full BC DVD and the Phase-2 unresolved counter is at ~70k (down from 583k pre-noise-reduction). But the path from 583k to 70k surfaced a recurring pattern of bugs whose fixes all looked similar:

| Bug | Surfaced as | Root cause |
|---|---|---|
| Page-field LHS `"No."` underlined as a Rec field access | Wrong cm-symbol-ref decoration | Generic walker treated `field("X"; Rec.X)` first arg as a bare identifier eligible for implicit-Rec field-access |
| Table-field declaration LHS stopped underlining after the page-field fix | Lost cm-symbol-decl decoration | `IsNonNavigableDeclarationKind` filtered every `kind="field"` — both page-control and table-field declarations |
| Underline landed on leftmost occurrence of the target name on a line | `field("No."; Rec."No.")` underlined the LHS even after the extractor emitted refs at the RHS column | `OeModuleReference` only stored `LineNumber`; `ListResolvablesInFileAsync` text-searched for the name |
| Right-click on a page-global var name fails | `FindRefsAtPosition ... no match` | Right-click resolver checks `oe_module_symbols` but not `oe_module_variables`, and `oe_module_variables` has no source positions anyway |
| `field()` inside SubPageLink / RunPageLink / TableRelation gets the layout-child first-arg-skip | LHS field name silenced when it should resolve | `field` keyword's meaning depends on whether it's a layout child or a property-value constructor; the walker has one branch for both |
| `column()` on a query vs `column()` on a report have different shapes | Generic handling fits neither perfectly | Same DSL keyword, different per-kind semantics |

Each individual fix is small. The pattern across them isn't.

## What the band-aids are working around

Three concrete structural issues:

### 1. The symbol table conflates declarations that differ by owner kind

`oe_module_symbols.kind = "field"` is used for:

- A column on a table (`field(N; "Name"; Type) { ... }`) — the canonical declaration of that table column. Find-references on it would surface every code site that reads or writes the field.
- A control on a page (`field("ControlName"; Rec."Field") { ... }`) — a page-local name. Find-references gives nothing useful; the actual table-field reference is on the RHS.

Same kind string, opposite navigability. The downstream filter (`IsNonNavigableDeclaration` in `SourceFileViewer.razor`) had to add an owner-kind branch to disambiguate. The same shape applies to `action` (page-local) vs hypothetical `action` on a different host kind, `column` on report vs query, `value` on enum vs hypothetical other host.

Every consumer of `oe_module_symbols.kind` has to know "but if owner is X, this means Y." The kind itself doesn't carry enough information.

### 2. One walker handles two genuinely different grammars

`AlReferenceExtractor` is one class that tries to handle:

- **Procedural AL**: procedure / trigger bodies, scope frames, chain resolution, member dispatch. This part is **truly uniform across owner kinds** — a procedure body's grammar is identical whether the procedure lives on a Page, a Codeunit, or a Table.
- **Object-scope DSL**: `field(...)`, `part(...)`, `action(...)`, `area(...)`, `dataitem(...)`, `column(...)`, `value(...)`, `key(...)`, `addAfter(...)`, etc. **This part is wildly kind-specific.** `field(N; "X"; Type)` on a table and `field("X"; Rec.X)` on a page share a keyword and a paren shape; they share nothing else.

The walker currently disambiguates with peek-and-branch logic (`if first token after ( is a Number, treat as table-side; else page-side`). It works but every new kind-specific construct (we just added `part(Name; Page)`) becomes another branch. The patterns that should have been one shape per kind ended up as one shape with kind-conditional logic.

### 3. Two parallel resolution paths

- **Extraction-time resolution**: `EmitCallSiteReferencesAsync` walks every file, resolves what each token points at, persists the result in `oe_module_references`.
- **Click-time resolution**: `ReferenceSessionService.CreateAtPositionAsync` runs at right-click time. Its strategy chain (`ResolveObjectByNameAsync` / qualified `var.Member` lookup / `ResolveMemberSymbolInFileAsync` / fallback) re-figures-out what the click points at, with logic that doesn't always match what the extractor did.

When we add a resolution feature (e.g. "find global variables"), both paths need to gain it. We've forgotten the second one twice. The two should share a single `ReferenceResolver` so the extractor and right-click can't drift.

## Concrete fresh design

Not a full AST / Roslyn-style parser. AL is big — `Microsoft.Dynamics.Nav.CodeAnalysis.*` is ~100k LoC of compiler infrastructure that ships in the AL VS Code extension. We can't justify reproducing it. The proposal below is a structural restructure of what we already have, sized for the actual problems we've seen.

### Symbol storage: split kinds at the point of extraction

Replace the conflated kind strings at write time so every downstream consumer reads a context-free value.

| Current `kind` | Split into |
|---|---|
| `field` (on table / tableextension) | `table_field` |
| `field` (on page / pageextension / requestpage / report-requestpage) | `page_field` |
| `field` (on report-dataitem column) | `report_column` (separate from query column) |
| `action` (on page / pageextension) | `page_action` |
| `value` (on enum / enumextension) | `enum_value` |
| `column` (on query) | `query_column` |
| `key` (on table / tableextension) | `table_key` |
| `key` (on page-controlfieldgroup) | rare; defer until seen |

Updated `AlSymbolExtractor` emits the disambiguated kinds directly. `ReleaseImportService.EmitSymbols` no longer needs the `EmitSymbols` → "consumed extractor row" dedup gymnastics for table-vs-page fields. The filter at `SourceFileViewer.razor:IsNonNavigableDeclaration` becomes a flat list lookup again — no owner-kind branch.

**Migration**: backfill via re-import. The toolbox is alpha; re-imports of full BC DVDs take ~90 seconds. A scripted "drop all object-explorer data + re-import" path is fine. The schema doesn't need a column rename — the values in `kind` just change.

**Side benefit**: the kind alone tells a maintainer what they're looking at, without having to load the surrounding object's row.

### Walker: one shared procedural walker + per-kind object-scope extractors

Split `AlReferenceExtractor.Walker` along the procedural / declarative seam.

```
AlReferenceExtractor (orchestrator)
├── AlProcedureWalker          ← all procedure / trigger body work
│   ├── scope frames
│   ├── chain resolution
│   ├── implicit-Rec
│   ├── member dispatch via Resolver
│   ├── builtin-method filters (AlBuiltinMethods)
│   └── known-system-type silencing
│
└── per-kind structural extractors (one per OwnerKind)
    ├── AlPageStructure         ← layout / actions / part / field-control DSL
    ├── AlTableStructure        ← fields / keys / fieldgroups / permissions DSL
    ├── AlReportStructure       ← dataitem / column / requestpage DSL
    ├── AlXmlportStructure      ← textelement / tableelement / fieldattribute DSL
    ├── AlQueryStructure        ← elements / filters / orderby DSL
    ├── AlCodeunitStructure     ← properties (TableNo / Permissions) only; body is all procedures
    ├── AlEnumStructure         ← value DSL
    ├── AlPermissionSetStructure
    └── AlInterfaceStructure    ← procedure signatures only
```

Each per-kind structure handler knows EXACTLY what each DSL keyword in its grammar means. `field()` on `AlPageStructure` is unambiguously a page-control (skip control name, walk source expression). `field()` on `AlTableStructure` is unambiguously a table-field declaration (parse `(N; "Name"; Type)`, emit type ref).

When a per-kind extractor encounters a procedure / trigger body, it delegates to `AlProcedureWalker.WalkBody(tokenRange, scopeContext)`. The body grammar is owner-kind-agnostic, so the procedural walker stays unified. ~95% code reuse for the procedural part; per-kind logic concentrated where it belongs.

The narrow interface between orchestrator and per-kind:

```csharp
public interface IAlObjectStructureExtractor
{
    AlExtractionResult Extract(
        IReadOnlyList<AlToken> tokens,
        AlExtractContext context,
        AlProcedureWalker procedureWalker);
}
```

`AlReferenceExtractor.Extract` becomes a switch on `context.OwnerKind` → instantiates the right per-kind extractor → returns its result. The orchestrator owns nothing else.

**Resist the temptation to use inheritance for the per-kind extractors.** Each one has different state and different control flow; a base class with virtuals would just hide branching behind dispatch tables. Composition (per-kind extractor takes `AlProcedureWalker` as a constructor dependency) reads better.

**Don't pre-build extractors for kinds we don't see in practice.** `AlControlAddInStructure`, `AlProfileStructure`, `AlPageCustomizationStructure`, `AlEntitlementStructure` — add when first needed; until then, fall through to a `NullStructure` that just walks the body for procedure references.

### Source positions on every declaration / reference / variable

Add what's missing so the resolver doesn't have to text-search at click time:

- `ModuleReference.ColumnNumber` — **already added in PR #159.** Just leave it.
- `ModuleVariable.LineNumber` / `.ColumnStart` / `.ColumnEnd` — currently null. Source extractor learns to capture var-block declarations (object-scope `var` blocks). Enables Go-to-definition on page / codeunit globals like `SalesDocCheckFactboxVisible` — currently broken because we can't tell the resolver where to jump even if we find the variable.
- Optionally: `ModuleSymbol.OwnerKind` denormalised so `ListDeclarationsInFileAsync` doesn't need the join. Marginal; defer unless query becomes a hotspot.

Migration: nullable columns, backfill on re-import. Same shape as the `column_number` add already done.

### Unified `ReferenceResolver` shared between extractor and right-click

Lift the resolution logic that lives in both `AlReferenceExtractor` (Phase-2) and `ReferenceSessionService.CreateAtPositionAsync` (click-time) into a single class. Single source of truth for "what does this token at this position point at."

The signature both paths need is the same:

```csharp
public sealed class ReferenceResolver
{
    public ReferenceResolution? Resolve(
        ResolverContext ctx,
        int line,
        int column,
        string token,
        string? leftQualifier);
}

public sealed record ReferenceResolution(
    ResolutionTarget Target,
    string Reason);  // for the diagnostic log

public abstract record ResolutionTarget;
public sealed record CatalogObject(long ObjectId, string Kind, string Name) : ResolutionTarget;
public sealed record MemberSymbol(long SymbolId) : ResolutionTarget;
public sealed record GlobalVariable(long VariableId, long OwnerObjectId) : ResolutionTarget;
public sealed record PlatformVirtualTable(int Id, string Name) : ResolutionTarget;
public sealed record Unresolved(string Reason) : ResolutionTarget;
```

`AlReferenceExtractor` calls it during Phase-2 to decide what to emit. `ReferenceSessionService` calls it on right-click to decide what was clicked. Adding a new resolution strategy (e.g. "find globals in `oe_module_variables`") happens in one place; both paths gain it automatically.

The current `ReferenceSessionService` strategy chain (`ResolveObjectByNameAsync` / `ResolveMemberSymbolAsync` / `ResolveMemberSymbolInFileAsync` / fallbacks) moves into `ReferenceResolver` as the implementation; the session service shrinks to "decode click → call resolver → build session row from result."

### Right-click feature parity with the underline path

Specific gaps to close as part of the refactor (independent of architecture but easy to land alongside it):

- **Find global variables on the file's owner**: `ReferenceResolver` checks `oe_module_variables` for the file owner's globals when other strategies miss. Returns `GlobalVariable` resolution; `ReferenceSessionService` opens a session keyed off the variable's name + owner.
- **Find references on a global variable**: requires emitting `variable_use` refs at extraction time (a new `reference_kind`). Without these, "Find references" on a variable can only text-search. Emission point: any bare identifier in a procedure body whose name matches an in-scope global var.
- **Cross-page field resolution for `SubPageLink` / `RunPageLink`**: the LHS field name belongs to the *target* page's source-table, not the current Rec. The per-kind extractor for the owning page can resolve the part's PageRef → its SourceTable → field on that table. Hard without per-kind structure; trivial with.

## Out of scope

- **A full AL AST**. We accept that some constructs we don't model (with-do blocks, #if predicates, Variant type tracking, method-chain return-type tracking through library calls) will continue to drop references silently. These are tracked in `.design/al-reference-extractor-gaps.md`. The refactor doesn't aim to close those; it aims to make the structure work for what we DO model.
- **Using Microsoft's AL compiler binaries directly**. Tempting (it ships in the AL VS Code extension, would give us the FULL parser for free), but the redistribution + version-coupling story is too messy and we lose control over what we can do.
- **A separate query language for the references store** (Roslyn-like symbol-table queries). The current EF + recursive CTE approach scales well for our query patterns; over-engineering this is risky.
- **Real-time / IDE-grade incremental re-extraction**. Re-importing a release is fast enough; live editing isn't a use case.

## Priority and rough effort

In decreasing ROI per effort, suitable as a sequenced punch list for a planning agent:

1. **Split symbol kinds at storage** (`table_field` vs `page_field` etc.). ~1 day. Eliminates the entire category of "kind=field, but if owner is X" branches in filters, resolvers, search. Touches `AlSymbolExtractor`, `ReleaseImportService.EmitSymbols`, `SourceFileViewer.razor` filter, possibly the right-click resolver's symbol lookup. Migration: re-import.
2. **Position-augmented `ModuleVariable`**. ~1 day. Add `LineNumber` / `ColumnStart` / `ColumnEnd`. Source extractor extracts var-block positions. Unblocks Go-to-definition on globals. Migration: nullable columns + re-import.
3. **Unified `ReferenceResolver`**. ~2 days. Extract from both extractor and `ReferenceSessionService`. Add the global-variable strategy. Forces the two paths to stay in sync going forward.
4. **Per-kind object-scope extractors**. ~3–5 days. The big one. Splits ~1000 lines of the current `AlReferenceExtractor.Walker` into kind-specific files. Shared `AlProcedureWalker` extracted first as a refactor (no behaviour change), then per-kind extractors take it as a dependency. Tests rewritten to target individual extractors instead of the unified walker.
5. **Cross-page resolution for SubPageLink / RunPageLink LHS field names**. ~1 day after #4. Trivial once `AlPageStructure` exists; near-impossible before.
6. **`variable_use` reference emission**. ~1 day. New `reference_kind`, emitted from the procedural walker when a bare identifier resolves to an in-scope global. Migration: index addition.

Items 1–3 are mostly mechanical and each independently shippable. Item 4 is the load-bearing change; everything before it makes it cheaper, everything after it gets cheaper.

## What the existing PR #159 leaves in place

The noise-reduction PR adds a lot of allow-lists, system-type tables, platform-virtual-table mappings, DSL keyword silencing, and column-position storage. None of that should be thrown out by the refactor — most of it lives in `AlBuiltinMethods` (well-organised, doc-commented for maintainers) and `ReleaseImportService.PlatformVirtualTables` (sourced from hougaard.com). The refactor restructures the *walker*; the *catalogs* stay.

The `IsNonNavigableDeclaration` owner-kind branch in `SourceFileViewer.razor` became redundant once item 1 landed and was removed — it's now a flat lookup over the disambiguated kinds (`trigger`, `page_field`, `page_action`).

## Reference: current state at refactor start

For the planning agent: the noise-reduction work landed in PR #159 on branch `claude/no-underline-fields-and-triggers`. Phase-2 unresolved count dropped from 583k to ~70k. The Object Explorer is usable at v0.5 alpha. Known remaining gaps before the refactor:

- Variables in `Visible` / `Editable` / `Enabled` property values have no right-click resolution path.
- `SubPageLink` / `RunPageLink` field references don't resolve.
- A handful of `head-not-a-variable` samples for quoted-identifier global names (`File Content`, `Detailed Info`) — probably parser gaps in `ParseVarBlock`.
- `chain-step Token='AsInteger' on receiver=table:Bank Account` — the chain walker doesn't advance the receiver through enum-field types.

These can be addressed within the refactor (items 1–6 above) more naturally than as one-off band-aids on the current walker.
