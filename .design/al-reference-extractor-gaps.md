# AL reference extractor — known gaps

This document tracks the AL syntax + semantic patterns that the current `AlReferenceExtractor` (and its supporting `AlLexer` + import-pipeline integration) doesn't recover references for. It's a follow-up list to phase 2 — call-site / field-access extraction — which shipped in `claude/find-references-call-sites`.

**Status:** punch list. Nothing here is wrong by accident; each gap is documented because closing it has an implementation cost worth scheduling, not because it slipped through.

## What phase 2 covers

For context — so the gaps below stand against something concrete.

The extractor walks an AL source file tokenised by `AlLexer`, maintains a scope stack as it encounters `procedure` / `trigger` openers, and emits one `method_call` or `field_access` row per resolved member access:

- **Heads**: bare variable, type literal (`Customer.Insert(true)`), implicit `Rec` / `xRec` inside a table/page, and `Kind::"Name"` typed-literal (`Codeunit::"Sales-Post".Run(...)`).
- **Scope frames**: procedure-local var-block declarations + parameters innermost, object-scoped globals (from `oe_module_variables`) outermost. Locals shadow globals.
- **Chained access**: `a.b.c` walks the receiver type forward through field/method return types when those resolve to AL objects.
- **Built-in filter**: runtime methods (`Insert`, `Get`, `Find`, `SetRange`, `Run`, …) and system fields (`SystemId`, `SystemCreatedAt`, …) skip silently instead of inflating the unresolved counter. Curated in `AlBuiltinMethods`.
- **Numeric grammar**: hex, scientific, suffixed literals tokenise correctly even though the extractor never inspects values.
- **Compound assignment**: `+=`, `-=`, `*=`, `/=` are single tokens.

The reference rows go to `oe_module_references` with the new phase-1 columns (`target_member_name`, `target_member_kind`, `target_symbol_id`) populated; the source viewer's References tab groups them under "Calls" alongside the existing declarations / indirect-via-type buckets.

## Gaps, ordered by user-visible impact

### 1. ~~Tableextension methods on base-table receivers~~ — RESOLVED

Landed alongside the dependency-aware resolver. The resolver now walks extensions targeting the base type and returns the extension's `AlMember` tagged with `DeclaringType`; the extractor stamps the reference at the extension so Find references on the extension's declaration row finds the call. Base-declared members shadow same-named extension members, matching AL dispatch.

### 2. ~~Dependency-aware resolution / disambiguation~~ — RESOLVED

The resolver now reads each module's `DependenciesJson`, computes the transitive visible-AppId set per module, and filters type + extension lookups through it. A file in Base App can no longer accidentally reach into DK Core; same-named objects across unrelated modules disambiguate correctly. Built once per release and cached per module-id so all files in the same module share the resolver instance.

### 3. Cross-release shadowing in the resolver

**Pattern**: a release whose `ParentReleaseId` points at another release. A file in the child release calls `Customer.Insert()`; the Customer table lives in the parent release (Base App), not the child's own modules.

**Symptom**: `EmitCallSiteReferencesAsync` builds the type + member catalogs from the importing release only. The Customer lookup misses, the call drops. The new visibility set correctly *includes* the parent's AppIds (parsed from `DependenciesJson`), but the catalog itself doesn't span releases.

**Fix shape**: the existing `FindReferencesAsync` query already walks the parent-release chain via a recursive CTE — reuse that pattern when building the catalogs. Either: (a) query the same recursive CTE to enumerate "every object visible to this release" before populating `typesByName`/`membersByOwner`; or (b) leave the catalog single-release and accept the loss for now since users tend to import each BC DVD as a single release.

**Effort**: medium. The recursive CTE is already documented in `ObjectExplorerService.cs`; lifting it into the import path is mechanical but touches the catalog construction. ~half a day.

**Priority**: medium. In practice users import every module they care about into one release. The pattern hits operators who layer customer-specific extensions on a parent BC release — important when it lands, but not the common shape.

**Relation to amend (issue #216)**: the amend path re-runs `EmitCallSiteReferencesAsync` over the full release after each add (option-1 reindex; see `ReleaseImportService.AmendReleaseAsync`). That picks up call sites against modules added later *within the same release*, but it doesn't change the single-release catalog scope — a child-release file calling into a parent-release table still drops on amend the same way it does on first ingest. Closing this gap remains a separate change.

### 4. ~~Bare self-procedure calls~~ — RESOLVED

Landed alongside the Go-to-definition wiring for call sites. The walker now
recognises the `Identifier(` shape (no receiver), filters AL statement
keywords (`if (X) then`, `not (X)`) and the new
`AlBuiltinMethods.BareCallableFunctions` set (`Message`, `Error`, `Confirm`,
`Exit`, `Format`, …), then looks the name up as a member on the file's
owner object. On hit, emits a `method_call` reference at the bare-call
position — so a private helper like `IndentICAccount()` shows up in the
calling procedure's "Calls" bucket and right-click → Go to definition
resolves through the new `(fileId, line, word)` lookup in
`ObjectExplorerService.GoToDefinitionAsync`.

### 5. `with X do begin … end;` implicit receivers

**Pattern**:

```al
with Customer do begin
    "No." := 'C001';
    Validate("Name", 'Acme');
end;
```

Inside the `with` block, bare `"No."` is a field access on Customer and `Validate(...)` is a method call on Customer. No `.` operator on either site.

**Symptom**: extractor only matches `receiver.Member` chains; bare quoted-identifier or call patterns inside the `with` block don't get emitted. No reference rows.

**Fix shape**: substantial. The scope tracker would need a new frame type — "implicit receiver block" — pushed on `with` and popped at the matching `end`. Bare identifiers and quoted identifiers inside the block become member accesses on the implicit receiver. Bracket depth tracking is needed because nested `with` blocks layer.

**Effort**: real work. ~1 day to implement + tests. The mechanism is also a more general primitive that could later be reused for AL's `with-do` lambda syntax in records (`SalesLine.WithSalesHeader(SalesHeader, ...) do begin ... end`), if that ever lands.

**Priority**: lower for new BC code (Microsoft marked `with` obsolete in 2020 and many style guides disable it). Higher for legacy ingestion — any pre-2020 BC codebase will have `with` blocks throughout, and Find references on procedures used heavily inside `with` blocks will look anaemic.

### 6. `#if false … #endif` conditional compilation

**Pattern**:

```al
#if DEBUG
    DiagnosticsTracer.Trace('foo');
#endif
```

**Symptom**: the lexer treats each `#` line as a single `Directive` token consumed without inspection. The body between `#if` and `#endif` lexes normally and the extractor walks it. References inside an `#if false` block (or with a never-defined symbol) get emitted even though the AL compiler skips them.

**Fix shape**: parse the `#if` predicate enough to decide skip-or-keep, then suppress token emission for the skipped body. Real preprocessor would track `#define`/`#undef` state across files; a cheap approximation could just literal-match the predicate text against a hard-coded "always-skip" set (e.g. `#if false`, `#if 0`) and let everything else through.

**Effort**: small for the literal-match approximation, real work for a proper preprocessor. The approximation covers the common "debug-only block" pattern; ~2 hours.

**Priority**: low. The `#if` feature is genuinely rare in shipped BC code — Microsoft added preprocessor directives relatively recently and they're more common in test artifacts than production modules.

### 7. Method-result chained access through scalar returns

**Pattern**: `Cust.GetCustomFieldValue().Substring(1, 10)` — call a procedure that returns Text, then call Text's `Substring` on the result.

**Symptom**: extractor advances the receiver via the procedure's parsed return type. If the return type is `Text` / `Code[20]` / `Decimal` (scalar), `ResolveTypeByName` returns null on the next step and the chain terminates. The first `.GetCustomFieldValue()` emits a reference fine; `.Substring` does not.

**Mitigation in place**: `AlBuiltinMethods.TextMethods` is populated. If we ever extend `IAlTypeResolver` to return a synthetic "Text" type-ref for scalar returns, the existing built-in filter would catch `Substring` cleanly without emitting noise. Today the chain just stops.

**Fix shape**: introduce synthetic type-refs for AL scalar types — `Text`, `Code`, `Integer`, `Decimal`, `Boolean`, `DateTime`, `Guid`, `Date`, `Time`, `BigInteger`, `Duration` — that resolve to a sentinel `AlTypeRef` without an AppId/ObjectId. The chain advance recognises sentinels and consults the matching built-in set. The catalog and the on-disk schema don't change; this is purely a `CatalogResolver` extension.

**Effort**: small. Maybe 2 hours including tests. The `AlBuiltinMethods` lists already do most of the work.

**Priority**: low–medium. The shape is common in modern BC code (`MyHelper.GetCustomerName().Substring(1, 3)`) but the lost reference is the *second* hop, not the first — Find references on `GetCustomerName` still catches the call site, just not the chained `Substring` follow-up.

### 8. Variant types

**Pattern**: `var X: Variant; X := Cust; X.Validate(...);`

**Symptom**: `Variant` isn't in the type catalog. After `X := Cust;` the static type of `X` is still `Variant`; the extractor doesn't track assignments to refine that. The `.Validate(...)` chain has no receiver type and drops as unresolved.

**Fix shape**: deferred. Tracking assignments would require dataflow analysis — a much bigger lift than the structural extraction we have today. Realistically out of scope; Variant usage in BC is mostly a parameter type for `Validate(FieldRef; Variant)` style calls rather than a heavy local-storage pattern.

**Effort**: meaningful — closer to a small parser feature than a one-liner. ~2 days at least if we wanted to follow assignment chains.

**Priority**: lowest. Variant is an escape hatch most BC code uses sparingly. Cost is high relative to the call sites we'd recover.

## Lexer gaps (lower-impact, documented for completeness)

- **`#if` predicate inspection** — already covered above; the lexer emits each `#`-line as a single `Directive` token without grammar.
- **AL keyword recognition** — `procedure` / `var` / `begin` / `end` / etc. come through as plain `Identifier` tokens; the consumer matches on `Value`. Intentional — keeps the lexer agnostic. If a future consumer wanted true keyword tokens, the lexer could grow a post-pass that re-tags matching identifiers.
- **String spanning newlines** — the lexer bails at the newline inside a `"..."` or `'...'`. AL doesn't permit either, so any source that triggers this wouldn't compile.

## What we explicitly don't plan to do

- **Build a full AL AST**. The reference extractor doesn't need one; it needs to recognise member-access patterns with enough scope context to resolve receivers. A real AST adds significant cost without significant additional accuracy for *this* feature. If a future feature wants AL AST (e.g. quick-fix code transformations), revisit.
- **Reimplement Microsoft's AL compiler logic**. The grammar is large and Microsoft owns the canonical implementation. Our scanner approximates the parts that matter for cross-module reference recovery; the gaps listed above are the practical cost of that approximation.

## Suggested ordering

If picked up as a follow-up branch:

1. **Cross-release shadowing** (medium catalog refactor; impacts layered-release imports).
2. **Method-result chained access through scalar returns** (small sentinel-type addition).
3. **`with X do …`** (real scope work; legacy-code value).
4. **`#if` predicate inspection** (low-priority approximation).
5. **Variant tracking** (deferred indefinitely unless a real ROI surfaces).
