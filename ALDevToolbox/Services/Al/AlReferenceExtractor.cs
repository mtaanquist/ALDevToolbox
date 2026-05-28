using System;
using System.Collections.Generic;
using System.Linq;
using ALDevToolbox.Services.Al.Structure;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Walks an AL source file looking for member-access usages
/// (<c>receiver.Member(...)</c>, <c>receiver."Field"</c>) and emits one
/// <see cref="ExtractedReference"/> per resolved usage. The extractor
/// is the entry point for phase-2 procedure-level Find references —
/// every row it emits is a `method_call` or `field_access` reference
/// the source viewer's References tab can surface.
///
/// Resolution is scope-aware: the extractor maintains a per-procedure
/// stack of (variable name → type name) entries built by reading the
/// procedure header's parameters + local var block. Object-scoped
/// globals (already in <c>oe_module_variables</c>) act as the outermost
/// frame. <c>Rec</c> and <c>xRec</c> resolve to the file's owner object
/// when the owner is a table / page / report.
///
/// Receiver-type resolution falls through several strategies:
/// <list type="number">
///   <item>Variable lookup: the qualifier matches a local / parameter /
///         global variable — use its declared type.</item>
///   <item>Type literal: the qualifier matches a known object name in
///         the type catalog (e.g. <c>Customer.Insert(...)</c> where
///         <c>Customer</c> is itself a record type, not a variable).</item>
///   <item><c>Rec</c> / <c>xRec</c> → the file's owner object.</item>
///   <item>Otherwise: drop the reference. The receiver could be a system
///         type (HttpClient, JsonObject, …) we don't track; or it could
///         be the result of a chained expression we'd need a full parser
///         to handle. These are counted in the
///         <see cref="ExtractionStats.UnresolvedReceivers"/> bucket so
///         operators can measure the residual gap.</item>
/// </list>
///
/// Chained access (<c>a.b.c</c>) walks the receiver type forward through
/// the member catalog: if <c>b</c> is a record field whose type is
/// itself a record, the next step's receiver is that record's type;
/// if <c>b</c> is a procedure with a known return type, ditto for
/// method-result chains (<c>f().g()</c>).
///
/// Bare self-procedure calls (<c>DoStuff();</c> with no receiver,
/// invoking another procedure on the same owner object) are recognised:
/// when the head identifier isn't in scope as a variable and isn't an AL
/// system function (see <see cref="AlBuiltinMethods.BareCallableFunctions"/>),
/// it's looked up as a member on the file's owner object and a
/// <c>method_call</c> reference is emitted on hit. This is what makes
/// right-click → Go to definition resolve <c>IndentICAccount()</c>-style
/// internal calls in the source viewer.
///
/// Not handled in v1:
/// <list type="bullet">
///   <item>Procedure-overload disambiguation by parameter types.</item>
///   <item>Bare field/property access without a receiver
///         (<c>"No." := 'C001'</c> inside a <c>with</c> block) — see
///         gap #5 in <c>.design/al-reference-extractor-gaps.md</c>.</item>
///   <item>Lambdas / anonymous procedures (AL doesn't have them).</item>
/// </list>
///
/// <para><b>Internal structure</b> (see
/// <c>.design/al-reference-extractor-refactor.md</c>): the orchestrator
/// nested below owns the object-scope DSL dispatch (property values,
/// <c>field(…)</c> / <c>part(…)</c> declarations, the EventSubscriber
/// attribute, the DSL-keyword first-arg skip). Procedure / trigger
/// body grammar — scope frames, member chains, bare self-calls,
/// implicit-Rec field access, label uses — lives in
/// <see cref="AlProcedureWalker"/> and shares mutable state through
/// <see cref="AlExtractionState"/>. The orchestrator delegates into
/// the procedure walker on every body-side token; the walker calls
/// back via the dispatch delegate for arg-list re-entry.</para>
/// </summary>
public static class AlReferenceExtractor
{
    /// <summary>
    /// Runs the extractor on a source file. Pure function — no IO,
    /// no DB; the caller passes in everything via
    /// <see cref="AlExtractContext"/>.
    /// </summary>
    public static AlExtractionResult Extract(string source, AlExtractContext context)
    {
        if (string.IsNullOrEmpty(source))
        {
            return new AlExtractionResult(
                Array.Empty<ExtractedReference>(),
                new ExtractionStats(0, 0, Array.Empty<UnresolvedSample>()),
                Array.Empty<ExtractedSymbolScope>());
        }

        var tokens = AlLexer.Tokenize(source);
        var orchestrator = new Orchestrator(tokens, context);
        return orchestrator.Run();
    }

    /// <summary>
    /// Top-level dispatch for one extraction pass. Owns the object-scope
    /// branches and delegates everything inside a procedure / trigger
    /// body to <see cref="AlProcedureWalker"/>. Step 4 of the refactor
    /// will further split the object-scope branches into per-kind files
    /// (<c>AlPageStructure</c>, <c>AlTableStructure</c>, …).
    /// </summary>
    private sealed class Orchestrator
    {
        private readonly AlExtractionState _state;
        private readonly AlProcedureWalker _procedureWalker;
        private readonly IAlObjectStructureExtractor _structure;

        public Orchestrator(List<AlToken> tokens, AlExtractContext ctx)
        {
            _state = new AlExtractionState(tokens, ctx);
            _procedureWalker = new AlProcedureWalker(_state, ProcessOneToken);
            _structure = SelectStructure(ctx.OwnerKind, _state, _procedureWalker);
        }

        /// <summary>
        /// Owner kind → structure extractor dispatch. Listed here as a
        /// single static map so a maintainer can scan it without
        /// chasing per-extractor cross-references. Unknown / unmodelled
        /// kinds fall through to <see cref="AlNullStructure"/>. See
        /// <c>.design/al-reference-extractor-refactor.md</c> step 4.
        /// </summary>
        private static IAlObjectStructureExtractor SelectStructure(
            string ownerKind, AlExtractionState state, AlProcedureWalker procedureWalker)
        {
            return (ownerKind?.ToLowerInvariant()) switch
            {
                "table" or "tableextension" => new AlTableStructure(state, procedureWalker),
                "page" or "pageextension" or "requestpage" => new AlPageStructure(state, procedureWalker),
                "query" => new AlQueryStructure(state, procedureWalker),
                "report" or "reportextension" => new AlReportStructure(state, procedureWalker),
                "xmlport" => new AlXmlportStructure(state, procedureWalker),
                _ => new AlNullStructure(),
            };
        }

        public AlExtractionResult Run()
        {
            // Object-scope is the outermost frame; it holds the global
            // variables the import pipeline already extracted, plus the
            // owner type for Rec / xRec when applicable.
            _procedureWalker.BuildAndPushGlobalScope();

            // Per-kind pre-scan: lets xmlport (and any future kind with
            // forward-referenced lexical constructs) seed the outer
            // scope frame before bodies declared earlier in the file
            // run through the main dispatch.
            _structure.Prescan();

            while (_state.Pos < _state.Tokens.Count)
            {
                ProcessOneToken();
            }

            return new AlExtractionResult(
                _state.Refs,
                new ExtractionStats(_state.Resolved, _state.Unresolved, _state.UnresolvedSamples),
                _state.SymbolScopes);
        }

        /// <summary>
        /// Dispatches a single token. Extracted so
        /// <see cref="AlProcedureWalker.WalkBalancedParens"/> can reuse the
        /// same dispatch when walking inside a method call's argument
        /// list, ensuring references inside <c>Rec.Validate("X", Y.Z)</c>
        /// emit instead of being swallowed by a SkipBalancedParens.
        /// Advances <see cref="AlExtractionState.Pos"/> by at least one
        /// position each call.
        /// </summary>
        private void ProcessOneToken()
        {
            var tok = _state.Tokens[_state.Pos];

            // Modern AL files open with `namespace X.Y.Z;` and a sequence
            // of `using A.B.C;` directives. The chain walker would otherwise
            // treat each dotted name as a member chain on an unresolved
            // head — emitting one false unresolved per directive (~17 per
            // BC file × 17k files in a release). Consume the whole directive
            // up to the next `;` instead.
            if (_state.ScopeStack.Count == 1
                && tok.Kind == AlTokenKind.Identifier
                && (string.Equals(tok.Value, "namespace", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tok.Value, "using", StringComparison.OrdinalIgnoreCase)))
            {
                SkipToSemicolonAtTopLevel();
                return;
            }

            // Procedure / trigger heads start a new scope frame.
            if (AlProcedureWalker.IsScopeOpener(tok))
            {
                _procedureWalker.StartProcedureScope();
                return;
            }

            // Object-header `implements "Iface1", "Iface2"` clause on a
            // codeunit. Appears between the object name and the opening
            // `{`, at object scope, before any procedure body has been
            // entered. Emit one `implements_interface` reference per
            // listed interface so Find References on the interface (or
            // its methods) can walk back to the implementing codeunit.
            if (_state.ScopeStack.Count == 1
                && tok.Kind == AlTokenKind.Identifier
                && string.Equals(tok.Value, "implements", StringComparison.OrdinalIgnoreCase))
            {
                TryConsumeImplementsClause();
                return;
            }

            // Object-scope `var` keyword: scan label declarations
            // (`Name: Label '…';`) into the bottom frame so bare uses
            // of label vars in procedure bodies resolve through the
            // same scope-walking path field accesses do.
            if (_state.ScopeStack.Count == 1
                && tok.Kind == AlTokenKind.Identifier
                && string.Equals(tok.Value, "var", StringComparison.OrdinalIgnoreCase))
            {
                _procedureWalker.ScanObjectScopeLabels();
                return;
            }

            // Inside a procedure / trigger body, track nested block
            // depth. `begin` and `case` open blocks closed by `end`;
            // the procedure walker maintains the depth so the
            // procedure's own `end;` pops the scope frame, not any
            // nested `end`.
            if (_procedureWalker.TryHandleBlockDepth(tok)) return;

            // `with X do begin … end;` — deprecated but still used in
            // Microsoft's regional/banking modules (e.g. AMC Banking's
            // `with PaymentExportData do begin … end;`). Push a scope
            // frame that shadows Rec to X's type for the block, so bare
            // identifiers inside resolve as field accesses / methods on X.
            // Must run BEFORE the chain-candidate check so `with` doesn't
            // get dispatched as an identifier-then-dot or bare-self-call.
            if (_state.ScopeStack.Count > 1
                && tok.Kind == AlTokenKind.Identifier
                && string.Equals(tok.Value, "with", StringComparison.OrdinalIgnoreCase))
            {
                if (_procedureWalker.TryConsumeWithStatement()) return;
            }

            // Member-access chain candidates. Two shapes trigger:
            //   A. Identifier `.` Member …
            //   B. Identifier `::` QuotedIdentifier (or Identifier)
            //      `.` Member …  — the typed-literal head pattern.
            if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
            {
                if (_state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == ".")
                {
                    _procedureWalker.TryConsumeMemberChain();
                    return;
                }

                if (_state.Pos + 2 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.DoubleColon
                    && (_state.Tokens[_state.Pos + 2].Kind == AlTokenKind.Identifier
                        || _state.Tokens[_state.Pos + 2].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _procedureWalker.TryConsumeTypedLiteralChain();
                    return;
                }

                // Bare self-procedure call: `Identifier(` with no receiver.
                if (tok.Kind == AlTokenKind.Identifier
                    && _state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == "(")
                {
                    if (_procedureWalker.TryConsumeBareSelfCall()) return;
                }

                // Bare use of an in-scope Label-typed variable.
                if (_state.ScopeStack.Count > 1
                    && (tok.Kind == AlTokenKind.QuotedIdentifier || tok.Kind == AlTokenKind.Identifier))
                {
                    if (_procedureWalker.TryConsumeLabelUse()) return;
                }

                // Implicit-Rec field access.
                if (_state.ScopeStack.Count > 1
                    && (tok.Kind == AlTokenKind.QuotedIdentifier || tok.Kind == AlTokenKind.Identifier))
                {
                    if (_procedureWalker.TryConsumeImplicitFieldAccess()) return;
                }

                // Bare global-variable reference inside a procedure
                // body. Runs after implicit-Rec field access so a
                // global named the same as a Rec field doesn't
                // shadow the field (extremely unlikely in practice).
                if (_state.ScopeStack.Count > 1
                    && tok.Kind == AlTokenKind.Identifier)
                {
                    if (_procedureWalker.TryConsumeGlobalVariableUse()) return;
                }

                // Bare quoted-or-unquoted identifier inside the parens
                // of a record built-in that takes a field name (e.g.
                // `Item.FieldNo("Qty. on Assembly Order")`). The chain
                // walker sets CurrentFieldReceiver to the call's
                // receiver for the duration of those parens; this
                // hook emits field_access against it. No-op when not
                // in such a context.
                if (_state.ScopeStack.Count > 1
                    && (tok.Kind == AlTokenKind.Identifier
                        || tok.Kind == AlTokenKind.QuotedIdentifier))
                {
                    if (_procedureWalker.TryResolveFieldReceiverContext(tok)) return;
                }

                // Object-scope property: `Identifier = Value;`. Per-kind
                // extractor gets first chance (e.g. AlPageStructure
                // claims SubPageLink / RunPageLink to resolve cross-page
                // field names), then the orchestrator's shared
                // dispatch covers the rest (SourceTable, TableRelation,
                // …).
                if (_state.ScopeStack.Count == 1
                    && tok.Kind == AlTokenKind.Identifier
                    && _state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == "=")
                {
                    if (_structure.TryConsumeObjectScopeProperty(tok.Value)) return;
                    if (TryConsumeObjectScopeProperty()) return;
                }

                // Object-scope DSL constructs the per-kind structure
                // extractor owns (see Services/Al/Structure/*.cs). Per-
                // kind disambiguation lives here: `field(...)` on a
                // table is the canonical (id; name; type) declaration,
                // `field(...)` on a page is a control declaration with
                // a source-expression second arg. The structure
                // extractor handles the kind-specific shape; the
                // orchestrator's shared property handlers (below) cover
                // the rest. Falls through to the generic DSL-keyword
                // skip so kinds without dedicated structure extractors
                // (codeunit, query, report, xmlport, enum, interface,
                // permissionset) still get the layout/grouping-keyword
                // first-arg skip they need.
                if (_state.ScopeStack.Count == 1
                    && _structure.TryConsumeObjectScopeToken(tok))
                {
                    return;
                }

                // Generic DSL keyword first-arg skip for kinds the
                // structure extractor didn't handle. Catches
                // `dataitem(Name; Source)`, `column(Name; Source)`,
                // `value(N; Name)`, `textelement(...)`, etc. —
                // kind-specific structure extractors for these land in
                // follow-up work; until then the generic skip prevents
                // mis-emission. The second arg continues to walk
                // through the main dispatch so source-table / page
                // references inside the construct still emit.
                if (_state.ScopeStack.Count == 1
                    && tok.Kind == AlTokenKind.Identifier
                    && _state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == "("
                    && AlBuiltinMethods.IsObjectDslKeyword(tok.Value))
                {
                    _state.Pos++; // the DSL keyword itself
                    _state.SkipDslKeywordFirstArg();
                    return;
                }
            }

            // Method / object attribute at object-scope: `[X(...)]`
            // immediately before a procedure / trigger declaration.
            // EventSubscriber emits an event_publisher reference; every
            // other attribute (CommitBehavior, InherentPermissions,
            // InherentEntitlements, NonDebuggable, ServiceEnabled,
            // ExternalBusinessEvent, BusinessEvent, IntegrationEvent,
            // InternalEvent, TryFunction, Scope, Obsolete, Test,
            // HandlerFunctions, TransactionModel, …) is decorative for
            // reference-extraction purposes — skip the bracketed span
            // wholesale so identifiers like CommitBehavior inside the
            // attribute don't get dispatched as bare self-calls and
            // accrue spurious unresolved samples.
            if (_state.ScopeStack.Count == 1
                && tok.Kind == AlTokenKind.Punct && tok.Value == "[")
            {
                if (_state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                    && string.Equals(_state.Tokens[_state.Pos + 1].Value, "EventSubscriber", StringComparison.OrdinalIgnoreCase))
                {
                    TryConsumeEventSubscriberAttribute();
                    return;
                }

                SkipBracketedAttribute();
                return;
            }

            // Last-chance object-scope bare identifier hook: per-kind
            // extractors that track a current dataitem / tableelement
            // source resolve bare field references inside
            // `column(name; SourceField)` / `filter(name; SourceField)`
            // / `fieldelement(name; SourceField)` source expressions
            // here. Default no-op for kinds without dataitem context.
            if (_state.ScopeStack.Count == 1
                && (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier))
            {
                if (_structure.TryResolveObjectScopeBareIdentifier(tok)) return;
            }

            _state.Pos++;
        }

        // ── Object-scope property extraction ──────────────────────────

        /// <summary>
        /// At object-scope (outside any procedure / trigger body), AL
        /// declarations look like <c>PropertyName = Value;</c>. We
        /// recognise a small set of property names whose values reference
        /// other AL objects (SourceTable, LookupPageID, …) or fields
        /// (DataCaptionFields) and emit reference rows the source viewer
        /// can underline and Go-to-definition can resolve.
        ///
        /// Returns true when the property was recognised and consumed;
        /// false when the caller should fall through (the identifier was
        /// not a known property — could be a custom user property the
        /// extractor doesn't model, or any other object-scope token).
        ///
        /// Coverage:
        /// <list type="bullet">
        ///   <item>Single-object: SourceTable, LookupPageID, CardPageID,
        ///         DrillDownPageID, RunObject. Value is a name (quoted or
        ///         bare) optionally preceded by a kind keyword.</item>
        ///   <item>Field list: DataCaptionFields. Comma-separated field
        ///         names on the owner's table.</item>
        ///   <item>TableRelation: scan-and-emit every type-resolving
        ///         identifier in the value. Catches the simple form
        ///         (<c>TableRelation = Customer;</c>), the with-filter
        ///         form (<c>… where(…)</c>), and the conditional form
        ///         (<c>if (…) X else Y</c>) — for the conditional shape
        ///         every branch's table gets a row, which is what a
        ///         developer wants from "shortcut into the table".</item>
        ///   <item>Permissions: <c>tabledata "X" = rm, tabledata "Y" = m;</c>
        ///         emits one property_object per tabledata target.</item>
        /// </list>
        /// Deferred (need richer value parsing): CalcFormula's nested
        /// table.field + filter expressions, SourceTableView's filter
        /// expressions, where()-bound field refs in TableRelation.
        /// </summary>
        private bool TryConsumeObjectScopeProperty()
        {
            var head = _state.Tokens[_state.Pos];
            var name = head.Value;

            if (string.Equals(name, "SourceTable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "LookupPageID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "CardPageID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DrillDownPageID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "RunObject", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumeObjectReferenceProperty(name);
            }
            if (string.Equals(name, "DataCaptionFields", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumeDataCaptionFields();
            }
            if (string.Equals(name, "TableRelation", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumeTableRelation();
            }
            if (string.Equals(name, "Permissions", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumePermissions();
            }
            if (string.Equals(name, "AccessByPermission", StringComparison.OrdinalIgnoreCase))
            {
                // Same `tabledata "Name" = <rights>` value shape as
                // Permissions, just on a per-action / per-control
                // property instead of an object-level one.
                return TryConsumePermissions();
            }
            if (string.Equals(name, "CalcFormula", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumeCalcFormula();
            }
            if (string.Equals(name, "SourceTableView", StringComparison.OrdinalIgnoreCase))
            {
                return TryConsumeSourceTableView();
            }
            return false;
        }

        /// <summary>
        /// Handles single-object-reference properties — value is a name
        /// (quoted or bare) optionally preceded by an AL object kind
        /// keyword (e.g. <c>SourceTable = "Sales Header";</c> or
        /// <c>RunObject = Page "Customer Card";</c>). The reference is
        /// emitted at the line/column of the NAME token (not the keyword)
        /// so the source-viewer underline targets the clickable text.
        /// </summary>
        /// <summary>
        /// Consumes a codeunit header's <c>implements "Iface1", "Iface2"</c>
        /// clause and emits one <c>implements_interface</c> reference per
        /// listed interface. Cursor is positioned on the <c>implements</c>
        /// keyword on entry; on return it sits on the next non-name token
        /// (typically the opening <c>{</c>).
        ///
        /// Same-app interfaces resolve through the catalog; cross-app
        /// interfaces (e.g. a partner codeunit implementing a Base App
        /// interface) come back from <see cref="IAlTypeResolver.ResolveTypeByName"/>
        /// with the declaring app's AppId stamped on the row. When the
        /// catalog doesn't know the name we still emit a row with the
        /// owner's AppId — find-references will resolve through the
        /// reference's name fallback the same way <c>extends_target</c>
        /// does for same-app extensions.
        /// </summary>
        private void TryConsumeImplementsClause()
        {
            _state.Pos++; // past `implements`

            while (_state.Pos < _state.Tokens.Count)
            {
                var tok = _state.Tokens[_state.Pos];
                if (tok.Kind != AlTokenKind.Identifier && tok.Kind != AlTokenKind.QuotedIdentifier)
                {
                    // Header ends at `{` or any other punctuation.
                    return;
                }

                var resolved = _state.Ctx.Resolver.ResolveTypeByName(tok.Value, "interface");
                var targetAppId = resolved?.AppId ?? _state.Ctx.OwnerAppId;
                var targetName = resolved?.Name ?? tok.Value;
                _state.EmitReference(new ExtractedReference(
                    Line: tok.Line,
                    Column: tok.Column,
                    TargetAppId: targetAppId,
                    TargetObjectKind: "interface",
                    TargetObjectId: null,
                    TargetObjectName: targetName,
                    TargetMemberName: null,
                    TargetMemberKind: null,
                    ReferenceKind: "implements_interface"));
                _state.Resolved++;
                _state.Pos++;

                // Skip an optional comma to the next interface name.
                if (_state.At(","))
                {
                    _state.Pos++;
                    continue;
                }

                return;
            }
        }

        private bool TryConsumeObjectReferenceProperty(string propertyName)
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            // Optional AL object kind keyword (`Page`, `Codeunit`, etc.).
            string? kindHint = null;
            if (_state.Pos < _state.Tokens.Count && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                && AlExtractionState.IsAlObjectKeyword(_state.Tokens[_state.Pos].Value))
            {
                kindHint = _state.Tokens[_state.Pos].Value;
                _state.Pos++;
            }

            if (_state.Pos >= _state.Tokens.Count) { _state.SkipToSemicolon(); return true; }
            var nameTok = _state.Tokens[_state.Pos];
            if (nameTok.Kind != AlTokenKind.Identifier && nameTok.Kind != AlTokenKind.QuotedIdentifier)
            {
                _state.SkipToSemicolon();
                return true;
            }

            var target = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value);
            if (target is null)
            {
                // Target not in catalog (cross-release, unknown kind, etc.).
                // Don't bump unresolved counter — that bucket is for chain
                // receivers; property misses are common and noisy.
                _state.SkipToSemicolon();
                return true;
            }

            _state.EmitReference(new ExtractedReference(
                Line: nameTok.Line,
                Column: nameTok.Column,
                TargetAppId: target.AppId,
                TargetObjectKind: target.Kind,
                TargetObjectId: target.ObjectId,
                TargetObjectName: target.Name,
                TargetMemberName: null,
                TargetMemberKind: null,
                ReferenceKind: "property_object"));
            _state.Resolved++;

            // kindHint is intentionally unused at the row level — the
            // catalog lookup already returned the canonical kind.
            _ = kindHint;
            _state.SkipToSemicolon();
            return true;
        }

        /// <summary>
        /// Handles <c>DataCaptionFields = "No.", "Name", "Description";</c>
        /// style multi-field-reference properties. Each comma-separated
        /// identifier is a field on the owner's table (the owner itself
        /// for tables / tableextensions, the SourceTable for pages /
        /// pageextensions). Emits one <c>field_access</c> per recognised
        /// field — same kind as a bare <c>"No."</c> in a body, so the
        /// source-viewer underlines and Go-to-definition treat them
        /// identically.
        /// </summary>
        private bool TryConsumeDataCaptionFields()
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            var ownerTable = _procedureWalker.RecType();
            if (ownerTable is null) { _state.SkipToSemicolon(); return true; }

            while (_state.Pos < _state.Tokens.Count && !_state.At(";"))
            {
                var tok = _state.Tokens[_state.Pos];
                if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    var member = _state.Ctx.Resolver.ResolveMember(ownerTable, tok.Value);
                    if (member is not null
                        && AlExtractionState.IsFieldKind(member.Kind))
                    {
                        var targetOwner = member.DeclaringType ?? ownerTable;
                        _state.EmitReference(new ExtractedReference(
                            Line: tok.Line,
                            Column: tok.Column,
                            TargetAppId: targetOwner.AppId,
                            TargetObjectKind: targetOwner.Kind,
                            TargetObjectId: targetOwner.ObjectId,
                            TargetObjectName: targetOwner.Name,
                            TargetMemberName: member.Name,
                            TargetMemberKind: member.Kind,
                            ReferenceKind: "field_access"));
                        _state.Resolved++;
                    }
                    _state.Pos++;
                    continue;
                }
                // Skip commas, whitespace tokens we don't care about.
                _state.Pos++;
            }
            if (_state.At(";")) _state.Pos++;
            return true;
        }

        /// <summary>
        /// Greedy scan of a <c>TableRelation = …;</c> value: walks every
        /// token between <c>=</c> and <c>;</c>, emitting one
        /// <c>property_object</c> per identifier that resolves to a
        /// table in the catalog. Catches:
        ///   - bare: <c>TableRelation = Customer;</c>
        ///   - quoted: <c>TableRelation = "G/L Account";</c>
        ///   - dotted: <c>TableRelation = "G/L Account"."No.";</c> (only
        ///     the table portion emits — field portion is field-context
        ///     work for a follow-up)
        ///   - with filter: <c>TableRelation = Customer where(…);</c> →
        ///     emits Customer; the filter's <c>field(X)</c> references
        ///     stay deferred.
        ///   - conditional: <c>if (Type = const(Item)) Item."No." else
        ///     Resource."No.";</c> → emits Item and Resource (every
        ///     branch's table). const(…) values like <c>Item</c> the
        ///     enum value won't resolve as an object — they're skipped
        ///     silently.
        ///
        /// De-duplicates within one value so a repeated reference doesn't
        /// produce a stack of identical underlines.
        /// </summary>
        private bool TryConsumeTableRelation()
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            var seen = new HashSet<long>();
            while (_state.Pos < _state.Tokens.Count && !_state.At(";"))
            {
                var tok = _state.Tokens[_state.Pos];
                if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    var target = _state.Ctx.Resolver.ResolveTypeByName(tok.Value);
                    if (target is not null
                        && string.Equals(target.Kind, "table", StringComparison.OrdinalIgnoreCase))
                    {
                        // De-dupe by (line, column) so the same identifier
                        // spelled twice at different positions still emits
                        // twice, but the same token doesn't get re-emitted
                        // on iteration glitches.
                        var key = ((long)tok.Line << 20) | (uint)tok.Column;
                        if (seen.Add(key))
                        {
                            _state.EmitReference(new ExtractedReference(
                                Line: tok.Line,
                                Column: tok.Column,
                                TargetAppId: target.AppId,
                                TargetObjectKind: target.Kind,
                                TargetObjectId: target.ObjectId,
                                TargetObjectName: target.Name,
                                TargetMemberName: null,
                                TargetMemberKind: null,
                                ReferenceKind: "property_object"));
                            _state.Resolved++;
                        }
                    }
                }
                _state.Pos++;
            }
            if (_state.At(";")) _state.Pos++;
            return true;
        }

        /// <summary>
        /// Handles <c>Permissions = tabledata "Customer" = rm,
        /// tabledata "Sales Header" = X;</c>. The list is comma-separated;
        /// each entry begins with <c>tabledata</c> (or, rarely,
        /// <c>tabledata id</c> via numeric id, which we skip for the
        /// same reason as numeric typed-literal targets) followed by a
        /// table name and the <c>= &lt;rights&gt;</c> rights spec we
        /// don't care about. Emits one <c>property_object</c> row per
        /// tabledata target so each underlines and Go-to-definition
        /// jumps to the table declaration.
        /// </summary>
        private bool TryConsumePermissions()
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            while (_state.Pos < _state.Tokens.Count && !_state.At(";"))
            {
                var tok = _state.Tokens[_state.Pos];
                // Watch for `tabledata <Name>` pairs. Other entry types
                // (e.g. <c>tabledata 27 = rm</c> with a numeric id) miss
                // the catalog lookup and fall through silently.
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, "tabledata", StringComparison.OrdinalIgnoreCase))
                {
                    _state.Pos++;
                    if (_state.Pos >= _state.Tokens.Count) break;
                    var nameTok = _state.Tokens[_state.Pos];
                    if (nameTok.Kind == AlTokenKind.Identifier
                        || nameTok.Kind == AlTokenKind.QuotedIdentifier)
                    {
                        // "table" kind hint disambiguates table vs page name
                        // collisions: a `tabledata "General Ledger Setup"`
                        // entry always means the Table, but BC ships pages
                        // with the same setup names. Without the hint the
                        // resolver could pick the page (insertion order),
                        // the post-filter would drop it, and the user gets
                        // no underline.
                        var target = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value, "table");
                        if (target is not null
                            && string.Equals(target.Kind, "table", StringComparison.OrdinalIgnoreCase))
                        {
                            _state.EmitReference(new ExtractedReference(
                                Line: nameTok.Line,
                                Column: nameTok.Column,
                                TargetAppId: target.AppId,
                                TargetObjectKind: target.Kind,
                                TargetObjectId: target.ObjectId,
                                TargetObjectName: target.Name,
                                TargetMemberName: null,
                                TargetMemberKind: null,
                                ReferenceKind: "property_object"));
                            _state.Resolved++;
                        }
                        _state.Pos++;
                    }
                    continue;
                }
                _state.Pos++;
            }
            if (_state.At(";")) _state.Pos++;
            return true;
        }

        // Per-kind structure extractors (Services/Al/Structure/*.cs)
        // own TryConsumeFieldDeclaration, TryConsumePartDeclaration,
        // and EmitTypedReference. See step 4 of
        // .design/al-reference-extractor-refactor.md.

        /// <summary>
        /// Handles a flowfield's <c>CalcFormula = sum("G/L Entry".Amount
        /// where("G/L Account No." = field("No.")));</c>. Emits up to
        /// three reference shapes:
        /// <list type="bullet">
        ///   <item>The aggregator's queried table → <c>property_object</c>
        ///         on the table.</item>
        ///   <item>The optional <c>.&lt;field&gt;</c> following the
        ///         table → <c>field_access</c> on that table. Only used
        ///         by sum / min / max / average / lookup; count and
        ///         exist don't have a target field.</item>
        ///   <item>Each <c>&lt;field&gt; =</c> LHS inside <c>where(…)</c>
        ///         → <c>field_access</c> on the queried table.</item>
        /// </list>
        /// The user's primary motivation: clicking into the table being
        /// aggregated when investigating where a flowfield's value comes
        /// from. v1 leaves the RHS of where pairs alone — <c>field("X")</c>
        /// refers to a field on the owner (this) table and would require
        /// field-context tracking we don't yet have.
        /// </summary>
        private bool TryConsumeCalcFormula()
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            // Aggregator function name (sum / count / exist / lookup / …).
            // We don't validate which one — any Identifier followed by
            // `(` is acceptable; the structure inside is what we read.
            if (_state.Pos < _state.Tokens.Count && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier)
            {
                _state.Pos++;
            }
            if (!_state.At("(")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // (

            // Queried table: bare or quoted identifier as the first token
            // inside the aggregator's parens.
            AlTypeRef? queriedTable = null;
            if (_state.Pos < _state.Tokens.Count
                && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                    || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                var tableTok = _state.Tokens[_state.Pos];
                var resolved = _state.Ctx.Resolver.ResolveTypeByName(tableTok.Value);
                if (resolved is not null
                    && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
                {
                    queriedTable = resolved;
                    _state.EmitReference(new ExtractedReference(
                        Line: tableTok.Line,
                        Column: tableTok.Column,
                        TargetAppId: resolved.AppId,
                        TargetObjectKind: resolved.Kind,
                        TargetObjectId: resolved.ObjectId,
                        TargetObjectName: resolved.Name,
                        TargetMemberName: null,
                        TargetMemberKind: null,
                        ReferenceKind: "property_object"));
                    _state.Resolved++;
                }
                _state.Pos++;
            }

            // Optional `.<field>` immediately after the table name —
            // sum / min / max / average / lookup target a specific field.
            if (queriedTable is not null && _state.At("."))
            {
                _state.Pos++;
                if (_state.Pos < _state.Tokens.Count
                    && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                        || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _procedureWalker.EmitFieldAccessIfResolves(_state.Tokens[_state.Pos], queriedTable);
                    _state.Pos++;
                }
            }

            // Walk the rest of the value (up to the matching `)` of
            // the aggregator) extracting where()/sorting() field refs
            // on the queried table.
            if (queriedTable is not null)
            {
                EmitWhereSortingFieldRefs(queriedTable, stopAt: ";");
            }

            _state.SkipToSemicolon();
            return true;
        }

        /// <summary>
        /// Handles a page's <c>SourceTableView = sorting("No."),
        /// where("Document Type" = filter(Order));</c>. Field references
        /// inside the <c>sorting(…)</c> and <c>where(…)</c> clauses are
        /// on the page's SourceTable — same Rec binding the
        /// implicit-field-access handler uses.
        /// </summary>
        private bool TryConsumeSourceTableView()
        {
            _state.Pos++; // property name
            if (!_state.At("=")) { _state.SkipToSemicolon(); return true; }
            _state.Pos++; // =

            var receiver = _procedureWalker.RecType();
            if (receiver is not null)
            {
                EmitWhereSortingFieldRefs(receiver, stopAt: ";");
            }
            _state.SkipToSemicolon();
            return true;
        }

        /// <summary>
        /// Walks forward from the current position to a delimiter
        /// (<paramref name="stopAt"/>) emitting <c>field_access</c>
        /// references on <paramref name="receiver"/> for fields named
        /// inside <c>where(…)</c> and <c>sorting(…)</c> clauses. The
        /// rule: every identifier on the LHS of <c>=</c> inside a
        /// <c>where(…)</c> is a field; every comma-separated identifier
        /// inside <c>sorting(…)</c> is a field.
        ///
        /// Doesn't advance past <paramref name="stopAt"/> — callers
        /// terminate the value via their own SkipToSemicolon.
        /// </summary>
        private void EmitWhereSortingFieldRefs(AlTypeRef receiver, string stopAt)
        {
            while (_state.Pos < _state.Tokens.Count && !_state.At(stopAt))
            {
                var tok = _state.Tokens[_state.Pos];
                if (tok.Kind == AlTokenKind.Identifier
                    && (string.Equals(tok.Value, "where", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tok.Value, "sorting", StringComparison.OrdinalIgnoreCase))
                    && _state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == "(")
                {
                    var clauseKind = tok.Value;
                    _state.Pos += 2; // identifier + (
                    EmitFieldsInsideClause(receiver, clauseKind);
                    continue;
                }
                _state.Pos++;
            }
        }

        /// <summary>
        /// Reads tokens inside a <c>where(…)</c> or <c>sorting(…)</c>
        /// clause starting just after the opening <c>(</c>. Tracks
        /// paren depth so nested calls like <c>field("X")</c> or
        /// <c>filter('A'|'B')</c> on the RHS don't bleed into the
        /// next iteration. Stops at the matching closing <c>)</c>.
        /// </summary>
        private void EmitFieldsInsideClause(AlTypeRef receiver, string clauseKind)
        {
            bool whereClause = string.Equals(clauseKind, "where", StringComparison.OrdinalIgnoreCase);
            int depth = 0;
            // Track which side of `=` we're on inside a where pair:
            // expect-LHS at the start of each segment, flip to expect-
            // RHS after `=`, flip back at `,` (next pair).
            bool expectLhs = true;
            while (_state.Pos < _state.Tokens.Count)
            {
                var tok = _state.Tokens[_state.Pos];

                if (tok.Kind == AlTokenKind.Punct)
                {
                    if (tok.Value == "(")
                    {
                        depth++;
                        _state.Pos++;
                        continue;
                    }
                    if (tok.Value == ")")
                    {
                        if (depth == 0)
                        {
                            _state.Pos++;
                            return;
                        }
                        depth--;
                        _state.Pos++;
                        continue;
                    }
                    if (depth == 0)
                    {
                        if (tok.Value == "=")
                        {
                            expectLhs = false;
                            _state.Pos++;
                            continue;
                        }
                        if (tok.Value == ",")
                        {
                            expectLhs = true;
                            _state.Pos++;
                            continue;
                        }
                    }
                    _state.Pos++;
                    continue;
                }

                if (depth == 0
                    && (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier))
                {
                    // sorting(…) — every comma-separated identifier is a field.
                    // where(…) — only the LHS of each `=` pair.
                    bool emitField = whereClause ? expectLhs : true;
                    if (emitField)
                    {
                        _procedureWalker.EmitFieldAccessIfResolves(tok, receiver);
                    }
                    _state.Pos++;
                    continue;
                }

                _state.Pos++;
            }
        }

        // ── Top-of-file directive handling ─────────────────────────────

        private void SkipToSemicolonAtTopLevel()
        {
            // The directive name token itself.
            _state.Pos++;
            while (_state.Pos < _state.Tokens.Count && !_state.At(";")) _state.Pos++;
            if (_state.At(";")) _state.Pos++;
        }

        // ── EventSubscriber attribute extraction ───────────────────────

        /// <summary>
        /// Handles <c>[EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post",
        /// 'OnAfterPostSalesDoc', '', false, false)]</c>. Emits one
        /// <c>event_publisher</c>-kind reference row pointing from the
        /// containing subscriber to the publisher's
        /// <c>(object, event_name)</c>. The reference is what makes
        /// Find references on the publisher list its subscribers — the
        /// existing member-scoped query joins on
        /// <c>(TargetObjectName, TargetMemberName, TargetMemberKind)</c>
        /// so no query changes are needed downstream.
        ///
        /// AL accepts the event name (3rd arg) and the element name
        /// (4th arg) as either single-quoted strings (legacy) or bare
        /// identifiers (newer BC). The lexer normalises both — strings
        /// lose their quotes, identifiers come through as-is — so the
        /// parser doesn't have to distinguish.
        ///
        /// Numeric target id (<c>Codeunit::80</c>) isn't supported in
        /// v1: BC has used the named form (<c>Codeunit::"Sales-Post"</c>)
        /// for the last ~decade. If a real import surfaces the numeric
        /// shape we'd extend <see cref="IAlTypeResolver"/> with an
        /// id-based lookup.
        /// </summary>
        private void TryConsumeEventSubscriberAttribute()
        {
            var attrLine = _state.Tokens[_state.Pos].Line;
            var attrCol = _state.Tokens[_state.Pos].Column;
            _state.Pos++; // [
            _state.Pos++; // EventSubscriber

            if (!_state.At("("))
            {
                SkipPastAttributeClose();
                return;
            }

            var args = ReadParenArgs();
            if (args.Count < 3) { SkipPastAttributeClose(); return; }

            // arg[1]: typed-literal `Kind::Name` for the target object.
            // The catalog lookup keys on the name; the kind is implicit
            // from the catalog row.
            var targetName = ExtractTypedLiteralName(args[1]);
            if (string.IsNullOrEmpty(targetName))
            {
                SkipPastAttributeClose();
                return;
            }

            // arg[2]: event name. String (legacy) or Identifier (newer).
            var eventName = ExtractStringOrIdentifier(args[2]);
            if (string.IsNullOrEmpty(eventName))
            {
                SkipPastAttributeClose();
                return;
            }

            var target = _state.Ctx.Resolver.ResolveTypeByName(targetName);
            if (target is null)
            {
                // Cross-release / unknown / numeric id. Silent drop —
                // matches the broader policy for unresolved property
                // references; subscriber attributes against unknown
                // targets aren't a developer error worth reporting.
                SkipPastAttributeClose();
                return;
            }

            _state.EmitReference(new ExtractedReference(
                Line: attrLine,
                Column: attrCol,
                TargetAppId: target.AppId,
                TargetObjectKind: target.Kind,
                TargetObjectId: target.ObjectId,
                TargetObjectName: target.Name,
                TargetMemberName: eventName,
                TargetMemberKind: "event_publisher",
                ReferenceKind: "event_publisher"));
            _state.Resolved++;

            SkipPastAttributeClose();
        }

        /// <summary>
        /// Reads a parenthesised comma-separated argument list. Assumes
        /// <see cref="AlExtractionState.Pos"/> is at the opening <c>(</c>.
        /// Returns one token list per arg; leaves
        /// <see cref="AlExtractionState.Pos"/> at the token immediately
        /// after the matching <c>)</c>. Respects nested parens so
        /// embedded expressions don't truncate the split.
        /// </summary>
        private List<List<AlToken>> ReadParenArgs()
        {
            var args = new List<List<AlToken>>();
            if (!_state.At("(")) return args;
            _state.Pos++;
            var current = new List<AlToken>();
            int depth = 0;
            while (_state.Pos < _state.Tokens.Count)
            {
                if (_state.At("("))
                {
                    depth++;
                    current.Add(_state.Tokens[_state.Pos]);
                    _state.Pos++;
                    continue;
                }
                if (_state.At(")"))
                {
                    if (depth == 0)
                    {
                        args.Add(current);
                        _state.Pos++;
                        return args;
                    }
                    depth--;
                    current.Add(_state.Tokens[_state.Pos]);
                    _state.Pos++;
                    continue;
                }
                if (_state.At(",") && depth == 0)
                {
                    args.Add(current);
                    current = new List<AlToken>();
                    _state.Pos++;
                    continue;
                }
                current.Add(_state.Tokens[_state.Pos]);
                _state.Pos++;
            }
            // Reached EOF mid-attribute; salvage what we have.
            args.Add(current);
            return args;
        }

        /// <summary>
        /// Extracts the name from a <c>Kind::Name</c> typed-literal
        /// argument. Returns null when the shape doesn't match (e.g.
        /// numeric id, malformed). The kind itself isn't returned —
        /// the catalog row that the name resolves to carries the
        /// canonical kind.
        /// </summary>
        private static string? ExtractTypedLiteralName(List<AlToken> arg)
        {
            // Find the `::` token; the right-hand side is the name.
            for (int i = 0; i < arg.Count - 1; i++)
            {
                if (arg[i].Kind != AlTokenKind.DoubleColon) continue;
                var name = arg[i + 1];
                if (name.Kind == AlTokenKind.Identifier || name.Kind == AlTokenKind.QuotedIdentifier)
                {
                    return name.Value;
                }
                // Number form (Codeunit::80) — v1 doesn't resolve.
                return null;
            }
            return null;
        }

        /// <summary>
        /// Extracts the value from a single-token argument that's either a
        /// String literal (single-quoted in source — the lexer keeps the
        /// surrounding quotes in the token value, so we strip them here)
        /// or a bare Identifier. AL accepts both shapes for the
        /// event-name / element-name parameters since the move to "newer
        /// BC" syntax — same value on the wire either way.
        /// </summary>
        private static string? ExtractStringOrIdentifier(List<AlToken> arg)
        {
            foreach (var tok in arg)
            {
                if (tok.Kind == AlTokenKind.String)
                {
                    return UnquoteString(tok.Value);
                }
                if (tok.Kind == AlTokenKind.Identifier
                    || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    return tok.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Strips the surrounding single quotes the lexer keeps on String
        /// tokens. <c>'foo'</c> in source → <c>"'foo'"</c> from the lexer
        /// → <c>"foo"</c> here. Also un-escapes the doubled-quote
        /// convention (<c>'it''s'</c> → <c>it's</c>) so the value matches
        /// what's stored in <c>oe_module_symbols.Name</c> for the
        /// publisher procedure.
        /// </summary>
        private static string UnquoteString(string raw)
        {
            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                return raw.Substring(1, raw.Length - 2).Replace("''", "'");
            }
            return raw;
        }

        /// <summary>
        /// Advances <see cref="AlExtractionState.Pos"/> past the matching
        /// <c>]</c> of an attribute we couldn't fully parse. Tracks
        /// bracket depth so nested expressions don't terminate early.
        /// Used as the bailout path when an attribute's args don't match
        /// the expected shape.
        /// </summary>
        private void SkipPastAttributeClose()
        {
            int depth = 0;
            while (_state.Pos < _state.Tokens.Count)
            {
                if (_state.At("[")) depth++;
                else if (_state.At("]"))
                {
                    if (depth == 0) { _state.Pos++; return; }
                    depth--;
                }
                _state.Pos++;
            }
        }

        /// <summary>
        /// Skips a bracketed object-scope attribute (<c>[X(args)]</c>)
        /// starting at the current <c>[</c> token. Tracks nested
        /// bracket depth so a paired-inner shape closes cleanly.
        /// Used to walk past procedure / object attributes that don't
        /// yield reference rows (CommitBehavior, InherentPermissions,
        /// NonDebuggable, …) without letting the identifiers inside
        /// fall through to the bare-self-call or chain dispatch and
        /// surface as spurious unresolved samples.
        /// </summary>
        private void SkipBracketedAttribute()
        {
            if (!_state.At("[")) return;
            _state.Pos++;
            int depth = 1;
            while (_state.Pos < _state.Tokens.Count && depth > 0)
            {
                if (_state.At("[")) depth++;
                else if (_state.At("]")) depth--;
                _state.Pos++;
            }
        }
    }
}

// ── Public DTOs ───────────────────────────────────────────────────────

/// <summary>
/// Context passed into <see cref="AlReferenceExtractor.Extract"/>. The
/// owner triplet identifies the file's containing object; the global
/// variable map and the resolver are how the extractor reaches outside
/// the file for type information.
/// </summary>
public sealed record AlExtractContext(
    string OwnerKind,
    string OwnerName,
    int? OwnerObjectId,
    Guid OwnerAppId,
    IReadOnlyDictionary<string, ResolvedVariableType> GlobalVars,
    IAlTypeResolver Resolver,
    string? OwnerSourceTableName = null,
    string? OwnerExtendsName = null);

/// <summary>
/// Looks up type information used during receiver-type resolution.
/// Implementations are typically backed by per-release lookup tables
/// built once at the start of import, but the interface stays narrow
/// so tests can stub it with hand-curated dictionaries.
/// </summary>
public interface IAlTypeResolver
{
    /// <summary>
    /// Resolves an AL type name (e.g. <c>Customer</c>, <c>Sales-Post</c>)
    /// to its location in the type catalog. Returns null when the name
    /// doesn't match a known object — common for system types like
    /// <c>HttpClient</c> or <c>JsonObject</c>.
    ///
    /// <paramref name="expectedKeyword"/> is the AL type keyword the
    /// caller has from context (<c>Record</c>, <c>Codeunit</c>,
    /// <c>Page</c>, <c>Report</c>, …), or null when no kind hint is
    /// available (e.g. bare identifier with no qualifying keyword).
    /// Implementations use it to disambiguate name collisions: a page's
    /// SourceTable named <c>Sales Header</c> resolves to the Table, not
    /// to a TableExtension someone happened to give the same name.
    /// </summary>
    AlTypeRef? ResolveTypeByName(string typeName, string? expectedKeyword = null);

    /// <summary>
    /// Resolves a member on a known owner. The owner is identified by
    /// its triplet; the member is matched by name (case-insensitive).
    /// When multiple symbols share the name (overloads), implementations
    /// should pick a stable one (typically the first declared) — the
    /// reference row records the name + kind, not a specific overload.
    /// </summary>
    AlMember? ResolveMember(AlTypeRef owner, string memberName);

    /// <summary>
    /// Returns the SourceTable name for an AL object that has one
    /// (page, pageextension, requestpage, etc.), or <c>null</c> for
    /// kinds that don't carry a source table (tables, codeunits, ...)
    /// or when the metadata isn't available.
    ///
    /// Used by per-kind structure extractors that need to resolve
    /// cross-object field references — e.g. <c>AlPageStructure</c>'s
    /// <c>SubPageLink</c> handler keys field names off the TARGET
    /// page's source table, not the current page's Rec (step 5 of
    /// <c>.design/al-reference-extractor-refactor.md</c>).
    ///
    /// Default <c>null</c> implementation so existing stub resolvers
    /// (unit tests, snapshot catalogs) don't need to opt in — they
    /// can override only when a test exercises a cross-object lookup.
    /// </summary>
    string? ResolveSourceTableName(AlTypeRef target) => null;
}

/// <summary>Resolved reference to an AL object type used by the receiver chain.</summary>
public sealed record AlTypeRef(Guid AppId, string Kind, int? ObjectId, string Name);

/// <summary>
/// A member resolved on an owner type. <see cref="ReturnTypeName"/> is
/// populated for procedures whose declared return type maps to another
/// AL object — used to advance the receiver type through chained calls.
///
/// <see cref="DeclaringType"/> is set when the member actually lives on
/// a *different* object than the static receiver type — the common case
/// is a tableextension adding a procedure or field to a base table. The
/// resolver returns the base-table receiver but tags the member as
/// declared by the extension; the extractor stamps the emitted
/// reference's target as the extension so a Find references on the
/// extension's declaration row finds the call.
/// </summary>
public sealed record AlMember(
    string Name,
    string Kind,
    string? ReturnTypeKeyword,
    string? ReturnTypeName,
    AlTypeRef? DeclaringType = null);

/// <summary>
/// One method-call or field-access reference the extractor recovered
/// from source. Coordinates point at the MEMBER name's start (not the
/// receiver), so the source-viewer flash highlights the right token.
/// </summary>
public sealed record ExtractedReference(
    int Line,
    int Column,
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string? TargetMemberName,
    string? TargetMemberKind,
    string ReferenceKind,
    string? SourceMemberName = null,
    string? SourceMemberKind = null,
    int? SourceMemberLine = null);

/// <summary>Per-file extraction statistics — used for diagnostic logging.</summary>
public sealed record ExtractionStats(
    int ResolvedReferences,
    int UnresolvedReceivers,
    IReadOnlyList<UnresolvedSample> UnresolvedSamples);

/// <summary>
/// A single unresolved reference captured for diagnostic logging. The
/// extractor caps these per-file so a pathological file doesn't blow
/// out memory; the import pipeline aggregates a small bucket across
/// files and logs the first N at end-of-phase so operators can spot
/// patterns (e.g. systematic gaps for a specific token shape, a
/// particular catalog name missing, …).
///
/// <para><b>Reasons:</b></para>
/// <list type="bullet">
///   <item><c>head-not-in-scope</c> — the chain's head identifier wasn't
///     a known variable, parameter, scope-frame entry, or catalog type.
///     Common cases: aliases, with-do shadowing, types from packages we
///     haven't ingested yet.</item>
///   <item><c>typed-literal-name</c> — <c>Kind::"Name"</c> where Name
///     didn't resolve as Kind. Usually a cross-release reference we
///     don't see in this release's catalog.</item>
///   <item><c>chain-step</c> — a <c>.member</c> didn't resolve on the
///     known receiver. Most often a tableextension/pageextension we
///     don't model, or an event-published procedure we haven't linked.</item>
///   <item><c>bare-call</c> — an unqualified identifier followed by
///     <c>(</c> that wasn't a system function, in-scope variable, or
///     own-member. Often a procedure on a dependency object we don't
///     surface from this owner's scope.</item>
/// </list>
/// </summary>
public sealed record UnresolvedSample(
    string Reason,
    string Token,
    int Line,
    int Column,
    string? ReceiverKind = null,
    string? ReceiverName = null,
    Guid? ReceiverAppId = null);

/// <summary>
/// Body-bearing symbol scope captured during the walk. One entry per
/// <c>procedure</c> / <c>trigger</c> / event publisher / event subscriber
/// whose matching <c>end;</c> was reached. The import service resolves
/// each entry back to the <c>oe_module_symbols</c> row it stamps
/// <c>end_line</c> / <c>end_column</c> on, via the
/// <c>(Kind, Name, StartLine)</c> tuple — the same tuple the persistence
/// layer uses for overload matching.
/// </summary>
public sealed record ExtractedSymbolScope(
    string Kind,
    string Name,
    int StartLine,
    int EndLine,
    int EndColumn);

/// <summary>Result envelope: extracted rows plus the run's stats.</summary>
public sealed record AlExtractionResult(
    IReadOnlyList<ExtractedReference> References,
    ExtractionStats Stats,
    IReadOnlyList<ExtractedSymbolScope> SymbolScopes);
