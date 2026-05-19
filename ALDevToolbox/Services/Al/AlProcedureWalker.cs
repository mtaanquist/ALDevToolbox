using System;
using System.Collections.Generic;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Shared mutable state for one <see cref="AlReferenceExtractor.Extract"/>
/// invocation. Threaded through both the object-scope orchestrator
/// (private nested type in <see cref="AlReferenceExtractor"/>) and the
/// procedure-body walker (<see cref="AlProcedureWalker"/>) so they can
/// mutate a single token cursor, reference list, scope stack, and
/// diagnostic counters without copying.
///
/// This is internal plumbing for the refactor described in
/// <c>.design/al-reference-extractor-refactor.md</c>. The split lets the
/// procedural body grammar — which is uniform across owner kinds — live
/// in <see cref="AlProcedureWalker"/>, while the object-scope DSL
/// dispatch stays on the orchestrator. Step 4 of the refactor will
/// further split the object-scope branch into per-kind extractors that
/// also hold this state.
/// </summary>
internal sealed class AlExtractionState
{
    /// <summary>
    /// Cap per-file unresolved samples so a pathological file
    /// (machine-generated, dependency-on-an-uningested-app) doesn't
    /// balloon memory. The import pipeline reservoir-merges a smaller
    /// global cap on top of these.
    /// </summary>
    public const int UnresolvedSampleCap = 20;

    public List<AlToken> Tokens { get; }
    public AlExtractContext Ctx { get; }
    public Stack<ScopeFrame> ScopeStack { get; } = new();
    public List<ExtractedReference> Refs { get; } = new();
    public List<UnresolvedSample> UnresolvedSamples { get; } = new();

    /// <summary>
    /// Body-bearing symbol scopes captured when their matching <c>end;</c>
    /// is reached. Populated in <see cref="AlProcedureWalker.TryHandleBlockDepth"/>
    /// at the moment the frame pops, so each entry has both endpoints
    /// stamped. Consumed by <c>ReleaseImportService</c> to fill the
    /// <c>end_line</c> / <c>end_column</c> columns on
    /// <c>oe_module_symbols</c> via <c>(Kind, Name, StartLine)</c> match.
    /// </summary>
    public List<ExtractedSymbolScope> SymbolScopes { get; } = new();
    public int Pos;
    public int Resolved;
    public int Unresolved;

    public AlTypeRef? OwnerTypeCache;
    public bool OwnerTypeResolved;
    public AlTypeRef? RecTypeCache;
    public bool RecTypeResolved;

    /// <summary>
    /// Receiver context for the bare field-name arguments inside a
    /// record built-in method's parens — e.g. inside
    /// <c>Item.Validate("Qty. on Assembly Order", 0)</c> this is set
    /// to the Item table for the duration of the parens so the bare
    /// quoted identifier <c>"Qty. on Assembly Order"</c> resolves as
    /// a <c>field_access</c> on Item (which then walks the extension
    /// chain to Asm. Item if needed). Saved + restored at the call
    /// boundary, so nested chains like
    /// <c>OuterRec.Validate(OtherRec.FieldNo("X"), 0)</c> get the
    /// right per-call receiver. Null when not inside such a call —
    /// the dispatch hook silently no-ops.
    /// </summary>
    public AlTypeRef? CurrentFieldReceiver;

    public AlExtractionState(List<AlToken> tokens, AlExtractContext ctx)
    {
        Tokens = tokens;
        Ctx = ctx;
    }

    // ── Token utilities ─────────────────────────────────────────────

    public bool At(string punct) =>
        Pos < Tokens.Count
        && Tokens[Pos].Kind == AlTokenKind.Punct
        && Tokens[Pos].Value == punct;

    public bool IsIdentifierTok(int idx, string name) =>
        idx < Tokens.Count
        && Tokens[idx].Kind == AlTokenKind.Identifier
        && string.Equals(Tokens[idx].Value, name, StringComparison.OrdinalIgnoreCase);

    public void SkipWhitespaceTokens()
    {
        // The lexer already discards whitespace, but Directive tokens
        // (#pragma) sit in the middle of declarations sometimes.
        while (Pos < Tokens.Count && Tokens[Pos].Kind == AlTokenKind.Directive) Pos++;
    }

    public void SkipToSemicolon()
    {
        while (Pos < Tokens.Count && !At(";")) Pos++;
        if (At(";")) Pos++;
    }

    /// <summary>
    /// Records an extracted reference, automatically stamping the
    /// owning procedure / trigger scope onto the row when the walker
    /// is inside one. Call sites pass the raw <see cref="ExtractedReference"/>
    /// (without the trailing <c>SourceMember*</c> fields); this helper
    /// reads the top scope frame and attaches the owning member's
    /// name + kind + start line so <c>ReleaseImportService</c> can
    /// resolve <c>source_symbol_id</c> on the persisted row without a
    /// line-range scan. Object-scope emissions pass through untouched.
    /// </summary>
    public void EmitReference(ExtractedReference reference)
    {
        if (ScopeStack.Count > 1)
        {
            var frame = ScopeStack.Peek();
            if (frame.SymbolName is not null && frame.SymbolKind is not null)
            {
                reference = reference with
                {
                    SourceMemberName = frame.SymbolName,
                    SourceMemberKind = frame.SymbolKind,
                    SourceMemberLine = frame.SymbolStartLine,
                };
            }
        }
        Refs.Add(reference);
    }

    /// <summary>
    /// Skips a <c>[…]</c> attribute starting at the current <c>[</c>
    /// token. Tracks bracket depth so a nested
    /// <c>[Foo([Bar])]</c> shape closes cleanly. No-op when the
    /// cursor isn't at <c>[</c>. Used by the procedure walker's
    /// var-block and parameter parsers — attributes can appear
    /// between <c>var</c> and a variable declaration
    /// (<c>[SecurityFiltering(SecurityFilter::Filtered)]</c>) and
    /// without explicit skipping the declaration's name gets
    /// mis-read as the attribute's name and the actual variable
    /// disappears from scope.
    /// </summary>
    public void SkipAttribute()
    {
        if (!At("[")) return;
        Pos++; // past [
        int depth = 1;
        while (Pos < Tokens.Count && depth > 0)
        {
            if (At("[")) depth++;
            else if (At("]")) depth--;
            Pos++;
        }
    }

    public void SkipBalancedParens()
    {
        if (!At("(")) return;
        int depth = 0;
        do
        {
            if (At("(")) depth++;
            else if (At(")")) depth--;
            Pos++;
        } while (Pos < Tokens.Count && depth > 0);
    }

    /// <summary>
    /// Consumes the first argument of a page / table / report DSL
    /// keyword's parens. The first arg is always a declaration name
    /// (control / part / action / group / area / repeater / cuegroup /
    /// etc.) — OR an unresolvable reference target on
    /// <c>modify(Name)</c> / <c>addAfter(Name)</c> / <c>addLast(Name)</c>
    /// where Name refers to a control in the base page we don't have
    /// a way to look up. Either way it isn't a navigation target,
    /// and walking it through the regular dispatch mis-emits
    /// references (e.g. <c>"No."</c> in <c>field("No."; Rec."No.")</c>
    /// getting picked up as an implicit-Rec field access on the
    /// page's source table).
    ///
    /// Stops at the first <c>;</c> at depth 0 (separator before
    /// the next arg) or <c>)</c> (end of the call) — leaves
    /// <see cref="Pos"/> pointing JUST PAST that separator so the
    /// regular dispatch picks up the second / source-expression
    /// argument.
    /// </summary>
    public void SkipDslKeywordFirstArg(bool alreadyPastOpenParen = false)
    {
        if (!alreadyPastOpenParen)
        {
            if (!At("(")) return;
            Pos++; // (
        }
        int depth = 0;
        while (Pos < Tokens.Count)
        {
            var tok = Tokens[Pos];
            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "(") { depth++; Pos++; continue; }
                if (tok.Value == ")")
                {
                    if (depth == 0) { Pos++; return; }
                    depth--;
                    Pos++;
                    continue;
                }
                if (tok.Value == ";" && depth == 0)
                {
                    Pos++;
                    return;
                }
            }
            Pos++;
        }
    }

    // ── Diagnostics ─────────────────────────────────────────────────

    /// <summary>
    /// Captures an unresolved reference up to <see cref="UnresolvedSampleCap"/>
    /// per file. The receiver type (when known) is recorded as a
    /// <c>kind:name</c> pair so the import-side aggregator can show
    /// "all chain-steps on table:Customer that failed" without re-running.
    /// Pass a <paramref name="receiverNameOverride"/> when the receiver
    /// isn't a resolved AlTypeRef (e.g. a typed-literal whose name didn't
    /// resolve — the caller has only the unresolved name to record).
    /// </summary>
    public void CaptureUnresolved(
        string reason, AlToken tok, AlTypeRef? receiver, string? receiverNameOverride = null)
    {
        if (UnresolvedSamples.Count >= UnresolvedSampleCap) return;
        UnresolvedSamples.Add(new UnresolvedSample(
            Reason: reason,
            Token: tok.Value,
            Line: tok.Line,
            Column: tok.Column,
            ReceiverKind: receiver?.Kind,
            ReceiverName: receiver?.Name ?? receiverNameOverride,
            // AppId pins which catalog object the resolver chose when
            // multiple modules ship same-named objects. Operators can
            // then SQL `oe_module_objects` + `oe_module_symbols` for
            // that exact (AppId, Kind, Name) triple to see whether the
            // catalog actually has the missing member.
            ReceiverAppId: receiver?.AppId));
    }

    // ── Static helpers ──────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="typeName"/> looks like a BC platform
    /// virtual-table numeric id. Microsoft reserves the range
    /// 2000000000..2000000999 for runtime-provided tables (Field,
    /// Company, User, NAV App Installed App, …); pages that set
    /// <c>SourceTable = 2000000206</c> end up with that numeric
    /// string as their source_table_name when the import-time
    /// numeric → name resolver doesn't have the id in its named
    /// map. Treating the whole range as silently unresolvable avoids
    /// the per-id enumeration burden — we don't have schemas for
    /// any of these tables anyway, so the lost diagnostic detail
    /// costs nothing.
    /// </summary>
    public static bool IsPlatformVirtualTableId(string typeName) =>
        int.TryParse(typeName, out var id)
        && id >= 2000000000
        && id <= 2000000999;

    /// <summary>
    /// True for any of the disambiguated field-kind values
    /// (<c>table_field</c>, <c>page_field</c>) the symbol extractor
    /// emits. Replaces the per-call <c>string.Equals(member.Kind, "field", …)</c>
    /// checks that pre-dated the kind split — keeps the resolver's
    /// "is this a field?" check stable as the persisted vocabulary
    /// expands. (See .design/al-reference-extractor-refactor.md
    /// step 1.) In practice the resolver only ever returns
    /// <c>table_field</c> on a real receiver — page fields aren't
    /// catalog members on tables — but accepting both keeps the
    /// helper safe to use anywhere a field-shape check is needed.
    /// </summary>
    public static bool IsFieldKind(string? kind) =>
        string.Equals(kind, "table_field", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "page_field", StringComparison.OrdinalIgnoreCase);

    public static bool IsAlObjectKeyword(string s) =>
        string.Equals(s, "Record", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Codeunit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Page", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Report", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Query", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "XmlPort", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Interface", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Enum", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "RequestPage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "TestPage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "TestPart", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "TestRequestPage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "ControlAddIn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "PermissionSet", StringComparison.OrdinalIgnoreCase)
        || string.Equals(s, "Profile", StringComparison.OrdinalIgnoreCase)
        // Confirmed against Microsoft's AL TextMate grammar
        // (microsoft/AL grammar/alsyntax.tmlanguage). DotNet types
        // are declared `var X: DotNet "System.Some.Type";` —
        // they won't resolve to AL objects in the catalog, but
        // we still need to recognise the keyword so the type
        // identifier that follows isn't picked up as the var name.
        || string.Equals(s, "DotNet", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One lexical scope frame in the procedure-body walker's stack. The
/// outermost frame is the object-scope frame (holds globals + Rec/xRec
/// when applicable); each <c>procedure</c> / <c>trigger</c> header
/// pushes a fresh frame whose <see cref="Vars"/> hold parameters and
/// local var-block declarations.
/// </summary>
internal sealed class ScopeFrame
{
    public Dictionary<string, ResolvedVariableType> Vars { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks how many <c>begin</c> blocks are currently open inside
    /// this procedure's body. The frame pops when the depth drops
    /// to zero (i.e. the matching `end;` of the body).
    /// </summary>
    public int BeginDepth { get; set; }

    /// <summary>
    /// Procedure / trigger name for the frame, or null on the outermost
    /// object-scope frame. Used by the reference emitters to stamp the
    /// owning member onto each <see cref="ExtractedReference"/> so the
    /// import service can resolve <c>source_symbol_id</c> without a
    /// line-range scan. Same string the symbol extractor records on
    /// <c>oe_module_symbols.name</c>.
    /// </summary>
    public string? SymbolName { get; set; }

    /// <summary>
    /// Symbol kind for the frame (<c>procedure</c>, <c>trigger</c>,
    /// <c>event_publisher</c>, etc.), aligned with the kinds the symbol
    /// extractor writes. Null on the outermost object-scope frame.
    /// </summary>
    public string? SymbolKind { get; set; }

    /// <summary>1-based line where the declaration's name token sits — the same line stamped on the matching <c>oe_module_symbols</c> row.</summary>
    public int SymbolStartLine { get; set; }
}

/// <summary>
/// Walks procedure and trigger bodies inside an AL source file: scope
/// frames (parameters + locals), member chains (<c>Receiver.Member…</c>
/// and the typed-literal <c>Kind::"Name".Member</c> form), bare
/// self-calls, label uses, implicit-Rec field access, and member
/// dispatch via the resolver.
///
/// The body grammar is owner-kind-agnostic — a procedure body's tokens
/// look the same whether the procedure lives on a Page, a Codeunit, or
/// a Table — so a single walker handles all cases. The companion
/// orchestrator (private <c>Walker</c> nested in
/// <see cref="AlReferenceExtractor"/>) dispatches object-scope DSL
/// constructs and calls into this walker once a body opens.
///
/// State is shared through <see cref="AlExtractionState"/>. The
/// orchestrator passes itself a <see cref="WalkBalancedParens"/>
/// callback (via <see cref="_dispatchOneToken"/>) so the inside of a
/// method-call's argument list re-enters the main dispatcher — keeps
/// nested references like <c>Rec.Validate("X", Customer."No.")</c>
/// emitting through the same code path the main loop uses.
/// </summary>
internal sealed class AlProcedureWalker
{
    private readonly AlExtractionState _state;
    private readonly Action _dispatchOneToken;

    public AlProcedureWalker(AlExtractionState state, Action dispatchOneToken)
    {
        _state = state;
        _dispatchOneToken = dispatchOneToken;
    }

    // ── Scope frames ────────────────────────────────────────────────

    /// <summary>
    /// Constructs and pushes the outermost (object-scope) frame onto
    /// the scope stack. Holds object-scope globals from
    /// <see cref="AlExtractContext.GlobalVars"/> plus the implicit
    /// <c>Rec</c> / <c>xRec</c> binding when the owner kind exposes
    /// one. Called once at the start of <c>Walker.Run</c>.
    /// </summary>
    public void BuildAndPushGlobalScope()
    {
        var frame = new ScopeFrame();
        foreach (var (name, type) in _state.Ctx.GlobalVars)
        {
            frame.Vars[name] = type;
        }
        // Rec / xRec — implicit receivers for record-bearing owners.
        // The exact binding depends on the owner kind:
        //   - table → Rec is the table itself.
        //   - tableextension → Rec is the BASE TABLE (the extension
        //     is conceptually merged into it at runtime). The
        //     importer threads ExtendsObjectName through as
        //     OwnerSourceTableName for tableextensions.
        //   - page / pageextension → Rec is the page's SourceTable
        //     (extensions inherit it from their base page).
        //   - codeunit with TableNo → Rec is the table named by
        //     TableNo. `codeunit "Gen. Jnl.-Post"` with
        //     `TableNo = "Gen. Journal Line"` runs against a
        //     journal-line record, with Rec bound to that table.
        //     Without TableNo, a codeunit has no implicit Rec.
        //   - report / reportextension / xmlport / query / requestpage —
        //     Rec is the owner itself for now; per-dataitem binding
        //     is a follow-up.
        // The importer denormalises SourceTable / TableNo /
        // ExtendsObjectName-of-tableextension onto
        // oe_module_objects.source_table_name; this method reads it
        // back through OwnerSourceTableName.
        var selfType = DetermineRecBinding();
        if (selfType is not null)
        {
            frame.Vars["rec"] = selfType;
            frame.Vars["xrec"] = selfType;
            // CurrFieldRef in field validate triggers, etc., is a
            // FieldRef — not a record — so we don't add it here.
        }
        _state.ScopeStack.Push(frame);
    }

    private ResolvedVariableType? DetermineRecBinding()
    {
        var k = _state.Ctx.OwnerKind?.ToLowerInvariant();

        // Codeunit: Rec only exists when TableNo binds it. Without
        // the binding, a codeunit has no Rec — most non-trigger
        // codeunits fall here, so returning null is correct.
        if (k == "codeunit")
        {
            if (string.IsNullOrEmpty(_state.Ctx.OwnerSourceTableName)) return null;
            return new ResolvedVariableType("Record", _state.Ctx.OwnerSourceTableName);
        }

        // Pages and tableextensions: Rec is the SourceTable / base.
        // Tableextension's SourceTable is set from ExtendsObjectName
        // at import time so the extension's Rec.<X> resolves through
        // the base table's catalog + extensions.
        if (k == "page" || k == "pageextension" || k == "tableextension")
        {
            if (string.IsNullOrEmpty(_state.Ctx.OwnerSourceTableName)) return null;
            return new ResolvedVariableType("Record", _state.Ctx.OwnerSourceTableName);
        }

        // Other record-bearing kinds: Rec is the owner itself
        // (tables, reports use dataitems per-trigger, but the
        // type-resolver lookup falls back to the owner name when
        // the catalog has no specific entry — same behaviour as
        // before this refactor).
        if (k == "table"
            || k == "report" || k == "reportextension"
            || k == "xmlport" || k == "query"
            || k == "requestpage")
        {
            return new ResolvedVariableType(_state.Ctx.OwnerKind!, _state.Ctx.OwnerName);
        }

        return null;
    }

    public static bool IsScopeOpener(AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return false;
        var v = tok.Value;
        return string.Equals(v, "procedure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "trigger", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts a fresh scope frame for a `procedure` or `trigger`
    /// declaration. Parses the parameter list and optional var block,
    /// then positions the cursor immediately after the opening
    /// `begin` so the main loop walks the body with the new frame
    /// active.
    /// </summary>
    public void StartProcedureScope()
    {
        // Capture the keyword kind before advancing — `procedure` or
        // `trigger`. The symbol extractor may later refine `procedure`
        // to `local_procedure` / `internal_procedure` / etc. via the
        // visibility prefix that sits BEFORE this keyword; reference-
        // emission only needs the broad bucket since the import service
        // matches scopes back to symbol rows by line number, not kind.
        string symbolKind = "procedure";
        if (_state.Pos < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
            && string.Equals(_state.Tokens[_state.Pos].Value, "trigger", StringComparison.OrdinalIgnoreCase))
        {
            symbolKind = "trigger";
        }

        // Skip past `procedure` / `trigger` keyword.
        _state.Pos++;

        // Skip scope keyword if procedure is annotated (local /
        // internal / protected). Actually the keyword sits BEFORE
        // `procedure`, so we don't expect to see it here; included
        // defensively for variant grammars.
        _state.SkipWhitespaceTokens();

        // Procedure / trigger name — for triggers like `OnValidate`
        // this is just an identifier; for procedures it's the
        // procedure name. Capture name + line for #181 scope tracking
        // so reference emitters can stamp the owning member onto each
        // ExtractedReference and the import service can resolve
        // source_symbol_id without a line-range scan.
        string? symbolName = null;
        int symbolStartLine = 0;
        if (_state.Pos < _state.Tokens.Count
            && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
        {
            symbolName = _state.Tokens[_state.Pos].Value;
            symbolStartLine = _state.Tokens[_state.Pos].Line;
            _state.Pos++;
        }

        var frame = new ScopeFrame
        {
            SymbolKind = symbolKind,
            SymbolName = symbolName,
            SymbolStartLine = symbolStartLine,
        };

        // Optional parameter list: `(name: Type; name: Type)`.
        _state.SkipWhitespaceTokens();
        if (_state.At("(")) ParseParameterList(frame);

        // Optional return-type clause: `: ReturnType` — no variables
        // introduced, just skip until we hit `var` or `begin`.

        // Optional local var block: `var name: Type; ...` between
        // procedure head and `begin`.
        while (_state.Pos < _state.Tokens.Count
               && !(_state.IsIdentifierTok(_state.Pos, "begin") || _state.IsIdentifierTok(_state.Pos, "var")))
        {
            _state.Pos++;
        }
        if (_state.IsIdentifierTok(_state.Pos, "var"))
        {
            _state.Pos++;
            ParseVarBlock(frame);
        }

        // Skip to and past `begin`.
        while (_state.Pos < _state.Tokens.Count && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            _state.Pos++;
        }
        if (_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            _state.Pos++;
            frame.BeginDepth = 1;
        }

        _state.ScopeStack.Push(frame);
    }

    private void ParseParameterList(ScopeFrame frame)
    {
        // Expect `(`. Walk until matching `)`. Each parameter is
        // [var] name [, name]* : Type [; ...].
        if (!_state.At("(")) return;
        _state.Pos++; // consume (

        while (_state.Pos < _state.Tokens.Count && !_state.At(")"))
        {
            // Attribute on a parameter (rare but legal — same shape
            // as var-block attributes). Skip before reading the name
            // so the attribute's identifier doesn't claim the
            // parameter slot.
            if (_state.At("["))
            {
                _state.SkipAttribute();
                continue;
            }

            // Optional `var` modifier on a parameter — pass by ref.
            if (_state.IsIdentifierTok(_state.Pos, "var")) _state.Pos++;

            // One or more comma-separated parameter names sharing a type.
            var names = new List<string>();
            while (_state.Pos < _state.Tokens.Count
                   && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                       || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                names.Add(_state.Tokens[_state.Pos].Value);
                _state.Pos++;
                if (_state.At(",")) { _state.Pos++; continue; }
                break;
            }

            // Expect `:` then Type.
            if (!_state.At(":")) { SkipToNextParam(); continue; }
            _state.Pos++;

            var type = ReadTypeReference();
            foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

            // Step past `;` if present.
            if (_state.At(";")) _state.Pos++;
        }
        if (_state.At(")")) _state.Pos++;
    }

    private void SkipToNextParam()
    {
        while (_state.Pos < _state.Tokens.Count && !_state.At(";") && !_state.At(")")) _state.Pos++;
        if (_state.At(";")) _state.Pos++;
    }

    private void ParseVarBlock(ScopeFrame frame)
    {
        // Inside `var ... begin`. Each declaration is
        // `name[, name]*: Type;`. Stop at `begin`.
        while (_state.Pos < _state.Tokens.Count && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            // Attributes on the variable declaration (the AL
            // [SecurityFiltering(SecurityFilter::Filtered)] shape).
            // Skip the entire [...] before parsing the next name —
            // otherwise the attribute's identifier gets mis-read as
            // the variable name and the actual declaration gets
            // swallowed by the SkipToSemicolonOrBegin recovery.
            if (_state.At("["))
            {
                _state.SkipAttribute();
                continue;
            }

            if (_state.Tokens[_state.Pos].Kind != AlTokenKind.Identifier
                && _state.Tokens[_state.Pos].Kind != AlTokenKind.QuotedIdentifier)
            {
                _state.Pos++;
                continue;
            }

            var names = new List<string>();
            while (_state.Pos < _state.Tokens.Count
                   && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                       || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                names.Add(_state.Tokens[_state.Pos].Value);
                _state.Pos++;
                if (_state.At(",")) { _state.Pos++; continue; }
                break;
            }

            if (!_state.At(":")) { SkipToSemicolonOrBegin(); continue; }
            _state.Pos++;

            var type = ReadTypeReference();
            foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

            if (_state.At(";")) _state.Pos++;
        }
    }

    private void SkipToSemicolonOrBegin()
    {
        while (_state.Pos < _state.Tokens.Count
               && !_state.At(";")
               && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            _state.Pos++;
        }
        if (_state.At(";")) _state.Pos++;
    }

    /// <summary>
    /// Reads a type reference like <c>Codeunit "Sales-Post"</c>,
    /// <c>Record Customer</c>, <c>Page "Customer Card"</c>,
    /// <c>Boolean</c>, <c>Code[20]</c>. Returns the (keyword,
    /// type name) pair the rest of the extractor cares about.
    /// Bare system types like <c>HttpClient</c> come back with a
    /// null Keyword — those won't resolve to AL objects later.
    ///
    /// When the type has an AL object keyword and the name
    /// resolves through the catalog, emits a <c>property_object</c>
    /// reference at the name token so var/parameter type names
    /// (e.g. <c>GLSetup: Record "General Ledger Setup"</c>) show
    /// up as clickable / underlinable like other resolved spans.
    /// The keyword is passed as a kind hint so collisions like
    /// table-and-page-of-the-same-name resolve deterministically.
    /// </summary>
    private ResolvedVariableType ReadTypeReference()
    {
        string? keyword = null;
        string typeName = string.Empty;
        int typeNameLine = 0;
        int typeNameColumn = 0;
        bool sawTypeName = false;

        if (_state.Pos >= _state.Tokens.Count) return new ResolvedVariableType(null, "");

        // First token is either an AL object keyword (Record /
        // Codeunit / Page / …) followed by the type name, or the
        // type identifier itself.
        var first = _state.Tokens[_state.Pos];
        if (first.Kind == AlTokenKind.Identifier && AlExtractionState.IsAlObjectKeyword(first.Value))
        {
            keyword = first.Value;
            _state.Pos++;
            if (_state.Pos < _state.Tokens.Count)
            {
                var t = _state.Tokens[_state.Pos];
                if (t.Kind == AlTokenKind.Identifier || t.Kind == AlTokenKind.QuotedIdentifier)
                {
                    typeName = t.Value;
                    typeNameLine = t.Line;
                    typeNameColumn = t.Column;
                    sawTypeName = true;
                    _state.Pos++;

                    // Fully-qualified namespaced type reference:
                    // `Record Microsoft.Manufacturing.StandardCost."Standard Cost Worksheet"`.
                    // Walk the dotted prefix and use the LAST segment
                    // as the actual type name — segments before it
                    // are the namespace path. Modern BC ships these
                    // increasingly often (every namespace-disambiguated
                    // cross-app reference uses this shape).
                    while (_state.Pos + 1 < _state.Tokens.Count
                           && _state.At(".")
                           && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                               || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        _state.Pos++; // past .
                        var segment = _state.Tokens[_state.Pos];
                        typeName = segment.Value;
                        typeNameLine = segment.Line;
                        typeNameColumn = segment.Column;
                        _state.Pos++;
                    }
                }
            }
        }
        else if (first.Kind == AlTokenKind.Identifier || first.Kind == AlTokenKind.QuotedIdentifier)
        {
            typeName = first.Value;
            _state.Pos++;
        }

        // Only emit when we have an explicit AL keyword. Bare
        // identifier types (Integer, Boolean, custom variables in
        // scope) aren't navigable AL objects and would either
        // mis-resolve or pollute the underline.
        if (keyword is not null && sawTypeName && !string.IsNullOrEmpty(typeName))
        {
            var target = _state.Ctx.Resolver.ResolveTypeByName(typeName, keyword);
            if (target is not null)
            {
                _state.EmitReference(new ExtractedReference(
                    Line: typeNameLine,
                    Column: typeNameColumn,
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

        // Length qualifier on a scalar (Code[20], Text[100]) —
        // we don't care, just skip.
        while (_state.Pos < _state.Tokens.Count && _state.At("["))
        {
            while (_state.Pos < _state.Tokens.Count && !_state.At("]")) _state.Pos++;
            if (_state.At("]")) _state.Pos++;
        }

        // Generic type parameters: `of [Type, Type, …]` on List /
        // Dictionary / built-in generic shapes. The contents don't
        // resolve to AL objects so the extractor never reads them,
        // but if we don't consume the `of [...]` tail here, the
        // var-block parser walks back into it and treats `of` as
        // the next variable name — which silently drops the
        // variable declared AFTER the generic-typed one. Bracket
        // depth tracking so `[Code[20], Integer]` nests cleanly.
        if (_state.IsIdentifierTok(_state.Pos, "of"))
        {
            _state.Pos++; // of
            if (_state.At("["))
            {
                int depth = 0;
                do
                {
                    if (_state.At("[")) depth++;
                    else if (_state.At("]")) depth--;
                    _state.Pos++;
                } while (_state.Pos < _state.Tokens.Count && depth > 0);
            }
        }

        return new ResolvedVariableType(keyword, typeName);
    }

    // ── Begin/case/end depth tracking ───────────────────────────────

    /// <summary>
    /// While inside a procedure / trigger body (<c>ScopeStack.Count &gt; 1</c>),
    /// the orchestrator delegates begin/case/end handling here so the
    /// procedure walker owns the depth counter and the matching pop.
    /// Returns true when the token was consumed.
    /// </summary>
    public bool TryHandleBlockDepth(AlToken tok)
    {
        if (_state.ScopeStack.Count <= 1 || tok.Kind != AlTokenKind.Identifier) return false;
        if (string.Equals(tok.Value, "begin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tok.Value, "case", StringComparison.OrdinalIgnoreCase))
        {
            _state.ScopeStack.Peek().BeginDepth++;
            _state.Pos++;
            return true;
        }
        if (string.Equals(tok.Value, "end", StringComparison.OrdinalIgnoreCase))
        {
            var frame = _state.ScopeStack.Peek();
            frame.BeginDepth--;
            if (frame.BeginDepth <= 0)
            {
                // Body just closed — capture the (start, end) span before
                // we lose the frame. The end token's line+column become
                // `end_line` / `end_column` on oe_module_symbols via
                // ReleaseImportService. SymbolName is only null on the
                // outermost object-scope frame, which TryHandleBlockDepth
                // never reaches (guarded by ScopeStack.Count > 1 above).
                if (frame.SymbolName is not null && frame.SymbolKind is not null)
                {
                    // `tok` is the `end` keyword; its Column is the column
                    // of `e`. EndColumn points PAST the last character so
                    // it lines up with how oe_module_symbols.column_end is
                    // recorded for declarations.
                    var endColumn = tok.Column + tok.Value.Length;
                    _state.SymbolScopes.Add(new ExtractedSymbolScope(
                        frame.SymbolKind,
                        frame.SymbolName,
                        frame.SymbolStartLine,
                        tok.Line,
                        endColumn));
                }
                _state.ScopeStack.Pop();
            }
            _state.Pos++;
            return true;
        }
        return false;
    }

    // ── Member-chain extraction ─────────────────────────────────────

    /// <summary>
    /// Reads a chain of <c>head.member.member…</c> starting at the
    /// current token, resolves the type at each step, and emits one
    /// reference per <c>.member</c> we manage to resolve. The cursor
    /// advances past the entire chain when we return.
    /// </summary>
    public void TryConsumeMemberChain()
    {
        // We arrive with Tokens[Pos] = head identifier, [Pos+1] = ".".
        var head = _state.Tokens[_state.Pos];
        _state.Pos++;

        // AL built-in static APIs like `CODEUNIT.Run(...)`,
        // `PAGE.RunModal(...)`, `NavApp.GetCurrentModuleInfo(...)`.
        // These are runtime dispatchers, not user variables or
        // catalog types — silently skip the chain (no reference
        // to emit, no diagnostic bump) but walk inside the args
        // via the main dispatch path so typed-literals like
        // `Codeunit::"Foo"` inside `CODEUNIT.Run(Codeunit::"Foo")`
        // still surface as references.
        if (AlBuiltinMethods.IsBuiltinStaticReceiver(head.Value))
        {
            while (_state.Pos < _state.Tokens.Count && _state.At("."))
            {
                _state.Pos++; // .
                if (_state.Pos < _state.Tokens.Count
                    && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                        || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _state.Pos++; // member
                }
                if (_state.Pos < _state.Tokens.Count && _state.At("(")) WalkBalancedParens();
            }
            return;
        }

        // Resolve the head to a type (a starting receiver). Two shapes:
        //   1. head matches a variable in any enclosing scope frame.
        //   2. head matches an object name in the type catalog (the
        //      `Customer.Insert(true)` pattern).
        // Anything else: this chain doesn't yield references.
        var receiverType = ResolveHeadType(head, out var declaredAsVar);
        if (receiverType is null)
        {
            // Variable's declared type is a known AL runtime / system
            // type (Dialog, RecordRef, XmlDocument, ModuleInfo, …) —
            // or it's declared with the `DotNet` keyword which never
            // resolves through the AL catalog by design — or it's a
            // BC platform virtual table by numeric id (the runtime
            // reserves 2000000000..2000000999 for these). Silence the
            // diagnostic so it doesn't crowd out real unresolveds.
            if (declaredAsVar is not null
                && (string.Equals(declaredAsVar.Keyword, "DotNet", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(declaredAsVar.TypeName)
                        && (AlBuiltinMethods.IsKnownSystemType(declaredAsVar.TypeName)
                            || AlExtractionState.IsPlatformVirtualTableId(declaredAsVar.TypeName)))))
            {
                AdvancePastChain();
                return;
            }

            // Implicit-Rec field as chain head: bare quoted (or bare)
            // identifier inside a table / page body that doesn't match
            // a scope variable or catalog type but DOES match a field
            // on Rec. Common shape on tables:
            //   `"Document Type".AsInteger()`
            // resolves head as Rec."Document Type" (enum field), then
            // the chain walker advances the receiver to the enum and
            // walks `.AsInteger()` as an enum built-in. Without this
            // fallback every implicit-Rec field used at chain-head
            // position fires head-not-a-variable.
            if (TryConsumeImplicitRecFieldChainHead(head))
            {
                return;
            }

            _state.Unresolved++;
            // Two sub-cases for the diagnostic samples: did the var
            // lookup miss entirely, or did it find a var whose
            // declared type didn't resolve through the resolver?
            // The latter points at visibility / catalog issues for
            // a known-named type — much more actionable than "name
            // isn't in scope".
            if (declaredAsVar is not null)
            {
                _state.CaptureUnresolved("head-var-type-unresolved", head, null,
                    receiverNameOverride: $"{declaredAsVar.Keyword ?? "?"} {declaredAsVar.TypeName}");
            }
            else
            {
                _state.CaptureUnresolved("head-not-a-variable", head, null);
            }
            AdvancePastChain();
            return;
        }

        WalkMemberChain(receiverType);
    }

    /// <summary>
    /// Reads a typed-literal head <c>Kind::"Name"</c> followed by
    /// <c>.member.member…</c>. The dominant BC pattern this catches:
    /// <c>Codeunit::"Sales-Post".Run(SalesHeader)</c>,
    /// <c>Page::"Customer Card".RunModal()</c>,
    /// <c>Report::"Customer - List".Run()</c>.
    /// </summary>
    public void TryConsumeTypedLiteralChain()
    {
        // We arrive with Tokens[Pos] = Kind identifier,
        // [Pos+1] = `::`, [Pos+2] = Name (quoted or bare).
        var kindTok = _state.Tokens[_state.Pos];
        _state.Pos++; // Kind
        _state.Pos++; // ::
        var nameTok = _state.Tokens[_state.Pos];
        _state.Pos++; // Name

        // Fully-qualified namespaced typed literal:
        //   `Database::Microsoft.Assembly.Document."Assembly Header"`
        // Take the LAST segment as the actual name; segments before
        // it are the namespace path. Same treatment ReadTypeReference
        // applies to var-block `Record Foo.Bar."Baz"` declarations.
        //
        // Critical lookahead: STOP when the next segment is followed
        // by `(` — that's a method call on the resolved type (e.g.
        // `Codeunit::"Sales-Post".Run(SalesHeader)`), not a namespace
        // continuation, and it has to be left to WalkMemberChain.
        // Without the guard the loop would greedily eat the `.Run`
        // chain step and mis-resolve "Run" as the type name.
        while (_state.Pos + 1 < _state.Tokens.Count
               && _state.At(".")
               && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                   || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier)
               && !(_state.Pos + 2 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 2].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 2].Value == "("))
        {
            _state.Pos++; // .
            nameTok = _state.Tokens[_state.Pos];
            _state.Pos++;
        }

        // Pass the kind keyword as a hint so name collisions across
        // object kinds disambiguate cleanly — `Codeunit::"Foo"`
        // should never resolve to a Table or Page named "Foo".
        var receiverType = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value, kindTok.Value);
        if (receiverType is null)
        {
            // `Kind::Value` where `Kind` isn't a canonical AL object
            // keyword is an enum value reference (e.g. `Verbosity::Error`,
            // `DataClassification::SystemMetadata`, `Step::Done`).
            // The catalog doesn't track enum values, so we can't
            // resolve the `Value` half — but the chain isn't broken,
            // it's just a value reference we don't model. Silence
            // the diagnostic when the kind name isn't one of the
            // recognised AL object keywords.
            if (!AlExtractionState.IsAlObjectKeyword(kindTok.Value)
                && !string.Equals(kindTok.Value, "DATABASE", StringComparison.OrdinalIgnoreCase))
            {
                AdvancePastChain();
                return;
            }
            _state.Unresolved++;
            _state.CaptureUnresolved("typed-literal-name", nameTok, null, kindTok.Value);
            AdvancePastChain();
            return;
        }

        // Emit a property_object reference at the typed-literal's
        // name token. Gives Find references / Go-to-definition / the
        // underline on `Codeunit::"Sales-Post"`, `Page::"Customer Card"`,
        // and `Database::Microsoft.Assembly.Document."Assembly Header"`
        // — same shape RunObject / SourceTable property values already
        // emit. Fires regardless of whether the typed literal is
        // standalone (`Database::"Foo"` as a parameter) or precedes
        // a chain (`Codeunit::"Sales-Post".Run(...)`); the
        // method_call on the chained `.Run` emits separately via
        // WalkMemberChain below.
        _state.EmitReference(new ExtractedReference(
            Line: nameTok.Line,
            Column: nameTok.Column,
            TargetAppId: receiverType.AppId,
            TargetObjectKind: receiverType.Kind,
            TargetObjectId: receiverType.ObjectId,
            TargetObjectName: receiverType.Name,
            TargetMemberName: null,
            TargetMemberKind: null,
            ReferenceKind: "property_object"));
        _state.Resolved++;

        // Walk `.member.member…` from the typed receiver, if any.
        WalkMemberChain(receiverType);
    }

    /// <summary>
    /// Walks the <c>.member.member…</c> tail of a chain after the
    /// head has been resolved to a receiver type. Emits one
    /// reference per step we resolve and advances the receiver to
    /// the member's return / declared type so chained access
    /// continues. Skips over method-call argument lists so they
    /// don't get misread as chain continuations.
    /// </summary>
    private void WalkMemberChain(AlTypeRef? receiverType)
    {
        while (receiverType is not null && _state.Pos < _state.Tokens.Count && _state.At("."))
        {
            _state.Pos++; // .

            if (_state.Pos >= _state.Tokens.Count) break;
            var memberTok = _state.Tokens[_state.Pos];
            if (memberTok.Kind != AlTokenKind.Identifier
                && memberTok.Kind != AlTokenKind.QuotedIdentifier)
            {
                break;
            }
            _state.Pos++;

            // Followed by ( → method_call. Anything else → field_access.
            var followedByParen = _state.Pos < _state.Tokens.Count && _state.At("(");
            var refKind = followedByParen ? "method_call" : "field_access";

            // Resolve member on receiver type.
            var member = _state.Ctx.Resolver.ResolveMember(receiverType, memberTok.Value);
            if (member is null)
            {
                // Differentiate "real unresolved" from "AL runtime
                // built-in that was never going to be in the
                // catalog" (Insert / Get / Find / SetRange etc.).
                // Built-ins skip silently and terminate the chain;
                // genuine unresolved targets bump the diagnostic
                // counter so operators can size the residual gap.
                if (AlBuiltinMethods.IsBuiltin(receiverType.Kind, memberTok.Value))
                {
                    // Built-in like Rec.Validate(...) — no ref to
                    // emit for the call itself, but the args may
                    // contain references that need to surface.
                    // Walk inside the parens via the same dispatch
                    // path the main loop uses.
                    if (followedByParen) WalkArgsForBuiltin(receiverType, memberTok.Value);
                    return;
                }
                // Synthesised platform virtual tables (Record Field,
                // Record Company, etc. — stamped with the PlatformAppId
                // sentinel of Guid.Empty in the catalog) have no
                // schemas: the import doesn't write members for them.
                // Field accesses like `TempFieldSet.TableNo` are
                // legitimate but unresolvable through our metadata.
                // Silence the diagnostic so it doesn't crowd out real
                // gaps; the trade-off (lost underline) was already
                // implicit when we synthesised the type.
                if (receiverType.AppId == Guid.Empty)
                {
                    if (followedByParen) WalkBalancedParens();
                    return;
                }
                _state.Unresolved++;
                _state.CaptureUnresolved("chain-step", memberTok, receiverType);
                AdvancePastRemainingChain();
                return;
            }

            // Members added by a tableextension/pageextension live
            // on the extension object, not the base. The resolver
            // signals that via member.DeclaringType; we stamp the
            // reference's target at the declaration site so Find
            // references on the extension's declaration row picks
            // this call up.
            var targetOwner = member.DeclaringType ?? receiverType;
            _state.EmitReference(new ExtractedReference(
                Line: memberTok.Line,
                Column: memberTok.Column,
                TargetAppId: targetOwner.AppId,
                TargetObjectKind: targetOwner.Kind,
                TargetObjectId: targetOwner.ObjectId,
                TargetObjectName: targetOwner.Name,
                TargetMemberName: member.Name,
                TargetMemberKind: member.Kind,
                ReferenceKind: refKind));
            _state.Resolved++;

            // For chained access, advance receiverType to the
            // member's result type (return type for a procedure,
            // declared type for a field). Stop the chain when the
            // member yields a non-record/non-object type or when
            // we can't resolve the next type.
            var preAdvanceReceiver = receiverType;
            receiverType = AdvanceReceiverByMember(member);

            // Walk the method call's argument list so references
            // embedded in the args (`Rec.Validate("X", Y.Z)` —
            // both the bare quoted "X" and the chained Y.Z) emit
            // their refs. Pos lands past the matching `)` on
            // return; the outer while loop's At(".") check then
            // handles any chain continuation that follows.
            if (followedByParen) WalkArgsForBuiltin(preAdvanceReceiver, memberTok.Value);
        }
    }

    /// <summary>
    /// Walks a method call's argument list, with bonus context: when
    /// the called method takes field-name arguments (Validate /
    /// SetRange / FieldNo / CalcFields / …), set
    /// <see cref="AlExtractionState.CurrentFieldReceiver"/> to the
    /// receiver for the duration of the parens so the bare-arg
    /// dispatch hook can resolve identifiers as field accesses on
    /// it. Properly nests: a nested call inside (like
    /// <c>OuterRec.Validate(InnerRec.FieldNo("X"), 0)</c>) saves and
    /// restores the slot so the inner call sees its own receiver,
    /// not the outer's.
    /// </summary>
    private void WalkArgsForBuiltin(AlTypeRef receiver, string methodName)
    {
        var saved = _state.CurrentFieldReceiver;
        _state.CurrentFieldReceiver =
            AlBuiltinMethods.FieldNameTakingMethods.Contains(methodName)
                ? receiver
                : null;
        try
        {
            WalkBalancedParens();
        }
        finally
        {
            _state.CurrentFieldReceiver = saved;
        }
    }

    /// <summary>
    /// Walks tokens inside a balanced <c>( … )</c> range, dispatching
    /// each through the orchestrator's main loop so references
    /// embedded in method arguments emit naturally. Called by
    /// <see cref="WalkMemberChain"/> after emitting a method-call
    /// reference, replacing the previous <c>SkipBalancedParens</c>
    /// that swallowed every arg-side reference (so
    /// <c>Rec.Validate("Sell-to Customer No.", Customer."No.")</c>
    /// was losing both arg references).
    ///
    /// Assumes <see cref="AlExtractionState.Pos"/> is at the opening
    /// <c>(</c>. On return, <see cref="AlExtractionState.Pos"/> is past
    /// the matching <c>)</c>.
    /// </summary>
    public void WalkBalancedParens()
    {
        if (!_state.At("(")) return;
        _state.Pos++; // past `(`
        int depth = 1;
        while (_state.Pos < _state.Tokens.Count)
        {
            if (_state.At("("))
            {
                depth++;
                _state.Pos++;
                continue;
            }
            if (_state.At(")"))
            {
                depth--;
                _state.Pos++;
                if (depth == 0) return;
                continue;
            }
            _dispatchOneToken();
        }
    }

    /// <summary>
    /// Bare self-procedure call detector: <c>DoStuff(...)</c> with no
    /// receiver. Three filters before emitting:
    ///   1. AL statement / operator keyword (<c>if</c>, <c>not</c>, …) —
    ///      these legitimately precede <c>(</c> without being calls.
    ///   2. AL system function (<c>Message</c>, <c>Error</c>, …) — skip
    ///      silently so we don't try to find them as self-members.
    ///   3. In-scope variable — bare-callable variables aren't a thing
    ///      in AL; advance without emitting.
    /// Hit case: the identifier resolves to a member on the file's
    /// owner type → emit a <c>method_call</c> reference and skip past
    /// the argument list.
    ///
    /// Returns true when the token was handled (cursor advanced),
    /// false when the caller should fall through to the default
    /// "advance one token" path.
    /// </summary>
    public bool TryConsumeBareSelfCall()
    {
        var head = _state.Tokens[_state.Pos];
        var name = head.Value;

        if (AlBuiltinMethods.IsStatementKeyword(name))
        {
            // Statement / operator keyword — let the default advance
            // walk past it. Returning false makes the main loop do
            // exactly one Pos++ next iteration; the `(` after it is
            // re-entered for whatever sits inside the parens.
            return false;
        }
        if (AlBuiltinMethods.IsBareCallable(name))
        {
            // AL system function — silent skip. We DON'T consume the
            // argument list: things like `Message(SalesHeader."No.")`
            // have real references inside the parens the main loop
            // still needs to walk.
            return false;
        }
        if (AlBuiltinMethods.IsObjectDslKeyword(name))
        {
            // Page / pageextension / tableextension / report /
            // xmlport / enum / permissionset declarative keyword —
            // `area(content)`, `field("X"; Rec."Y")`, `value(N; "Z")`,
            // `modify(SomeField) { … }`. These look identical to
            // procedure calls to the lexer but introduce nested
            // declarative structure; the arg list still walks
            // through the main loop so the inner expressions (a
            // page-field's `Rec."Y"` reference, for example)
            // continue to emit.
            return false;
        }

        // Implicit-Rec method call. On a page / pageextension /
        // tableextension / codeunit-with-TableNo body, a bare
        // `Insert();` is shorthand for `Rec.Insert();` and
        // `SetCurrentKey(X);` is `Rec.SetCurrentKey(X);`. The
        // RecordMethods list is the authoritative set of Record
        // built-ins, so the check is just "owner has Rec AND name
        // is a RecordMethod" → silent skip. Without this, every
        // implicit-Rec call in BC pages surfaces as a bare-call
        // unresolved.
        if (RecType() is not null && AlBuiltinMethods.RecordMethods.Contains(name))
        {
            return false;
        }

        // Fallback: even when Rec isn't bound, the bare call's name
        // might match a Record / Page / Codeunit / Common built-in
        // method. This catches mis-parsed chain calls (e.g. the
        // chain head got dropped earlier so `SomeRec.Insert(...)`
        // surfaces as bare `Insert(...)`), Rec-method shorthand in
        // contexts the explicit Rec check doesn't cover, and
        // text/variant-method bare uses (`Trim`, `Unwrap`,
        // `HasValue`, `AsInteger`). False-positive risk is a real
        // user procedure named after a built-in — vanishingly rare
        // in BC's corpus since the name would shadow the built-in.
        if (AlBuiltinMethods.RecordMethods.Contains(name)
            || AlBuiltinMethods.RecordSystemFields.Contains(name)
            || AlBuiltinMethods.CodeunitMethods.Contains(name)
            || AlBuiltinMethods.PageMethods.Contains(name)
            || AlBuiltinMethods.ReportMethods.Contains(name)
            || AlBuiltinMethods.XmlportMethods.Contains(name)
            || AlBuiltinMethods.QueryMethods.Contains(name)
            || AlBuiltinMethods.CommonMethods.Contains(name)
            || AlBuiltinMethods.TextMethods.Contains(name)
            || AlBuiltinMethods.CollectionMethods.Contains(name)
            || AlBuiltinMethods.JsonMethods.Contains(name))
        {
            return false;
        }

        // In-scope variable? AL doesn't allow calling a variable as
        // a function — if the name is in scope we treat it as
        // referenced and just advance past.
        var key = name.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.ContainsKey(key))
            {
                return false;
            }
        }

        // Try to resolve as a member on the file's owner object.
        var ownerType = OwnerType();
        if (ownerType is null) return false;

        var member = _state.Ctx.Resolver.ResolveMember(ownerType, name);
        if (member is null)
        {
            // Could be a bare AL system function we don't have on our
            // list yet, or a same-named procedure on a related extension
            // we don't track from this angle. Counted but not emitted.
            _state.Unresolved++;
            _state.CaptureUnresolved("bare-call", head, ownerType);
            return false;
        }

        // Tableextension-declared self-members fall through the
        // resolver's DeclaringType path same as receiver chains.
        var targetOwner = member.DeclaringType ?? ownerType;
        _state.EmitReference(new ExtractedReference(
            Line: head.Line,
            Column: head.Column,
            TargetAppId: targetOwner.AppId,
            TargetObjectKind: targetOwner.Kind,
            TargetObjectId: targetOwner.ObjectId,
            TargetObjectName: targetOwner.Name,
            TargetMemberName: member.Name,
            TargetMemberKind: member.Kind,
            ReferenceKind: "method_call"));
        _state.Resolved++;

        // Advance past the identifier only. The argument list is
        // walked by the main loop in its own right — `Outer(Cust.X())`
        // needs the inner `Cust.X()` chain to still be picked up.
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Lazily resolves the file owner's <see cref="AlTypeRef"/> via
    /// the resolver. Cached because bare-self-call detection can fire
    /// many times per file and the resolver lookup walks a dictionary
    /// each call.
    /// </summary>
    public AlTypeRef? OwnerType()
    {
        if (_state.OwnerTypeResolved) return _state.OwnerTypeCache;
        _state.OwnerTypeResolved = true;
        if (string.IsNullOrEmpty(_state.Ctx.OwnerName)) return null;
        // Pass the owner's catalog kind as the hint so bare self-calls
        // on a pageextension named the same as its base page (or any
        // similarly-named extension over its base) land on the
        // extension's own members, not on the base object's. Without
        // the hint, the resolver's non-extension preference would
        // pick the base, which is the wrong receiver for self-calls.
        _state.OwnerTypeCache = _state.Ctx.Resolver.ResolveTypeByName(_state.Ctx.OwnerName, _state.Ctx.OwnerKind);
        return _state.OwnerTypeCache;
    }

    /// <summary>
    /// Lazily resolves Rec's <see cref="AlTypeRef"/> — the receiver
    /// type implicit-field-access uses. For tables / tableextensions
    /// this is the owner type; for pages / pageextensions it's the
    /// SourceTable. <see cref="BuildAndPushGlobalScope"/> already
    /// encoded the choice in the bottom scope frame's <c>rec</c>
    /// entry; we read it back and resolve once.
    /// </summary>
    public AlTypeRef? RecType()
    {
        if (_state.RecTypeResolved) return _state.RecTypeCache;
        _state.RecTypeResolved = true;
        // Bottom frame (object-scope) is where Rec lives. Walk to it.
        ScopeFrame? bottom = null;
        foreach (var frame in _state.ScopeStack) bottom = frame;
        if (bottom is null) return null;
        if (!bottom.Vars.TryGetValue("rec", out var declared)) return null;
        if (string.IsNullOrEmpty(declared.TypeName)) return null;
        // Pass the keyword: Rec on a page is `Record` → must be a
        // table (not a tableextension someone named the same way).
        _state.RecTypeCache = _state.Ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
        return _state.RecTypeCache;
    }

    // ── Label declarations + uses ───────────────────────────────────

    private static readonly HashSet<string> ObjectSectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "procedure", "trigger", "fields", "actions", "keys",
        "layout", "dataset", "schema", "elements", "controls",
        "requestpage", "labels",
    };

    /// <summary>
    /// Object-scope <c>var</c> block: scans <c>Name: Label '…';</c>
    /// declarations and adds them to the bottom (object-scope) frame
    /// so bare uses inside any procedure / trigger body in the file
    /// resolve through the same scope-walk that fields and other
    /// vars use. Non-label declarations are skipped — those come in
    /// via the symbol-package globals already. Terminates at the
    /// closing <c>}</c> of the object or the next section keyword
    /// (<c>procedure</c>, <c>trigger</c>, <c>fields</c>, …).
    /// </summary>
    public void ScanObjectScopeLabels()
    {
        _state.Pos++; // var keyword

        // Find the bottom (object-scope) frame to mutate.
        ScopeFrame? bottom = null;
        foreach (var f in _state.ScopeStack) bottom = f;
        if (bottom is null) return;

        int braceDepth = 0;
        while (_state.Pos < _state.Tokens.Count)
        {
            var tok = _state.Tokens[_state.Pos];

            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "{") { braceDepth++; _state.Pos++; continue; }
                if (tok.Value == "}")
                {
                    if (braceDepth == 0) return;
                    braceDepth--;
                    _state.Pos++;
                    continue;
                }
            }
            if (braceDepth == 0
                && tok.Kind == AlTokenKind.Identifier
                && ObjectSectionKeywords.Contains(tok.Value))
            {
                return;
            }

            // Detect `Name : Label`. The lexer drops whitespace, so
            // the three tokens are adjacent in the stream.
            if ((tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                && _state.Pos + 2 < _state.Tokens.Count
                && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                && _state.Tokens[_state.Pos + 1].Value == ":"
                && _state.Tokens[_state.Pos + 2].Kind == AlTokenKind.Identifier
                && string.Equals(_state.Tokens[_state.Pos + 2].Value, "Label", StringComparison.OrdinalIgnoreCase))
            {
                var nameLower = tok.Value.ToLowerInvariant();
                if (!bottom.Vars.ContainsKey(nameLower))
                {
                    bottom.Vars[nameLower] = new ResolvedVariableType(null, "Label");
                }
                _state.Pos += 3;
                continue;
            }
            _state.Pos++;
        }
    }

    /// <summary>
    /// Emits a <c>label_use</c> reference when the bare identifier
    /// resolves to an in-scope variable typed <c>Label</c>. Target
    /// is the file's owner object + the label's name + kind
    /// <c>"label"</c>, so the existing member-scoped query (and
    /// import-time symbol-id stamping) lights up the Find references
    /// path identical to a procedure call.
    /// </summary>
    public bool TryConsumeLabelUse()
    {
        var tok = _state.Tokens[_state.Pos];
        var key = tok.Value.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (!frame.Vars.TryGetValue(key, out var declared)) continue;
            if (!string.Equals(declared.TypeName, "Label", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var owner = OwnerType();
            if (owner is null) return false;
            _state.EmitReference(new ExtractedReference(
                Line: tok.Line,
                Column: tok.Column,
                TargetAppId: owner.AppId,
                TargetObjectKind: owner.Kind,
                TargetObjectId: owner.ObjectId,
                TargetObjectName: owner.Name,
                TargetMemberName: tok.Value,
                TargetMemberKind: "label",
                ReferenceKind: "label_use"));
            _state.Resolved++;
            _state.Pos++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Implicit-Rec field access: a bare identifier or quoted
    /// identifier inside a body, with no qualifier. When the owner
    /// kind exposes Rec and the identifier matches a field on Rec's
    /// type, emit a <c>field_access</c> reference targeting that
    /// type. Followed-by-paren / followed-by-dot / followed-by-
    /// double-colon shapes are filtered by the caller (those go
    /// through the chain / typed-literal / bare-call paths).
    ///
    /// Filters (in order):
    ///   1. Rec must be in scope (RecType != null).
    ///   2. The identifier must not already match an in-scope variable
    ///      (parameter / local / global). AL resolves variables
    ///      first; we mirror that.
    ///   3. Bare identifiers (not quoted): drop statement keywords
    ///      (<c>if</c>, <c>then</c>, <c>not</c>, …), AL system
    ///      functions (<c>Message</c>, <c>Today</c>, …), and AL
    ///      literal-shaped keywords (<c>true</c>, <c>false</c>, …)
    ///      via <see cref="AlBuiltinMethods.IsStatementKeyword"/> /
    ///      <see cref="AlBuiltinMethods.IsBareCallable"/>.
    ///      Quoted identifiers bypass these — they're almost always
    ///      field names in AL bodies.
    ///   4. The identifier must resolve to a member on Rec's type
    ///      with kind = <c>field</c>. A method match doesn't emit
    ///      (those go through the bare-self-call path when
    ///      followed by parens, or are silently dropped here).
    ///
    /// Known limitation: inside a <c>with X do …</c> block the
    /// implicit receiver is X, not Rec. Gap #5 in
    /// <c>al-reference-extractor-gaps.md</c>; this handler would
    /// emit on Rec's type instead of X's. False positive only fires
    /// when both X and Rec have a same-named field — usually
    /// resolves to a silent drop.
    ///
    /// Returns true when the token was handled (cursor advanced),
    /// false when the caller should fall through to the default
    /// "advance one token" path.
    /// </summary>
    public bool TryConsumeImplicitFieldAccess()
    {
        var head = _state.Tokens[_state.Pos];
        var name = head.Value;

        // 1. Rec must exist.
        var rec = RecType();
        if (rec is null) return false;

        // 2. Skip when name is an in-scope variable.
        var key = name.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.ContainsKey(key)) return false;
        }

        // 3. For bare identifiers, additional keyword / system-function
        //    filters. Quoted identifiers bypass — they're field names.
        if (head.Kind == AlTokenKind.Identifier)
        {
            if (AlBuiltinMethods.IsStatementKeyword(name)) return false;
            if (AlBuiltinMethods.IsBareCallable(name)) return false;
        }

        // 4. Resolve as a field on Rec's type. Methods don't qualify
        //    here — those need parens to be a call site, and bare
        //    field-shaped accesses are what we're after.
        var member = _state.Ctx.Resolver.ResolveMember(rec, name);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind))
        {
            return false;
        }

        var targetOwner = member.DeclaringType ?? rec;
        _state.EmitReference(new ExtractedReference(
            Line: head.Line,
            Column: head.Column,
            TargetAppId: targetOwner.AppId,
            TargetObjectKind: targetOwner.Kind,
            TargetObjectId: targetOwner.ObjectId,
            TargetObjectName: targetOwner.Name,
            TargetMemberName: member.Name,
            TargetMemberKind: member.Kind,
            ReferenceKind: "field_access"));
        _state.Resolved++;
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Bare identifier in a procedure body that resolves to an
    /// object-scope global variable (not a local, not a Label, not
    /// Rec / xRec). Emits a <c>variable_use</c> reference targeted at
    /// the file's owner with <c>TargetMemberName = &lt;var&gt;</c>;
    /// the import-side stamps the actual <c>TargetVariableId</c> from
    /// <c>oe_module_variables</c>. Right-click "Find references" on
    /// a global then returns these rows in a single seek (filtered
    /// index on <c>target_variable_id</c>).
    ///
    /// Filters (in order):
    ///   1. Must be inside a procedure / trigger body
    ///      (<c>ScopeStack.Count &gt; 1</c>).
    ///   2. Name must resolve in the GLOBAL frame and NOT in any
    ///      inner (procedure-local / parameter) frame — locals
    ///      shadow globals in AL, same as every block-scoped
    ///      language.
    ///   3. Skip the special <c>rec</c> / <c>xrec</c> entries that
    ///      <see cref="BuildAndPushGlobalScope"/> seeds — they're
    ///      implicit-receiver shortcuts, not user-declared globals.
    ///   4. Skip Label-typed entries; <see cref="TryConsumeLabelUse"/>
    ///      runs ahead of this and handles them as <c>label_use</c>.
    ///
    /// Returns true when the token was consumed (cursor advanced),
    /// false to fall through. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 6.
    /// </summary>
    public bool TryConsumeGlobalVariableUse()
    {
        if (_state.ScopeStack.Count <= 1) return false;

        var tok = _state.Tokens[_state.Pos];
        var name = tok.Value;
        var key = name.ToLowerInvariant();

        // rec / xrec are special — not navigable globals.
        if (string.Equals(key, "rec", StringComparison.Ordinal)
            || string.Equals(key, "xrec", StringComparison.Ordinal))
        {
            return false;
        }

        // Walk innermost-first. The global frame is the LAST one
        // iterated. Hits in non-global frames mean the name is a
        // local / parameter — silently consume.
        ScopeFrame? matchFrame = null;
        ResolvedVariableType? matchType = null;
        ScopeFrame? globalFrame = null;
        foreach (var frame in _state.ScopeStack)
        {
            globalFrame = frame; // last-iterated wins
            if (matchFrame is null && frame.Vars.TryGetValue(key, out var declared))
            {
                matchFrame = frame;
                matchType = declared;
            }
        }
        if (matchFrame is null) return false;
        if (!ReferenceEquals(matchFrame, globalFrame)) return false;

        // Labels run through TryConsumeLabelUse before we get here,
        // but a bare Label identifier whose label-use path bailed
        // (e.g. no owner type) could still land here. Defer the
        // emission to the label path.
        if (matchType is not null
            && string.Equals(matchType.TypeName, "Label", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var owner = OwnerType();
        if (owner is null) return false;

        _state.EmitReference(new ExtractedReference(
            Line: tok.Line,
            Column: tok.Column,
            TargetAppId: owner.AppId,
            TargetObjectKind: owner.Kind,
            TargetObjectId: owner.ObjectId,
            TargetObjectName: owner.Name,
            TargetMemberName: name,
            TargetMemberKind: "global_variable",
            ReferenceKind: "variable_use"));
        _state.Resolved++;
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Resolves a bare identifier (typically quoted) as a field
    /// access on the current field-receiver context — set by
    /// <see cref="WalkArgsForBuiltin"/> while walking the arg list of
    /// a record built-in that takes field names (Validate / SetRange
    /// / FieldNo / CalcFields / …). Without this hook, inside
    /// <c>Item.FieldNo("Qty. on Assembly Order")</c> the bare quoted
    /// id <c>"Qty. on Assembly Order"</c> would fall through every
    /// dispatch (no chain head, no Rec for a codeunit owner, no
    /// matching scope variable) and no field_access reference would
    /// emit — Find references on the field's declaration row (often
    /// on a tableextension like <c>Asm. Item</c>) would miss the
    /// call. Returns true when consumed; false to fall through.
    ///
    /// Skips when the identifier matches a local / parameter /
    /// global variable in scope — AL's resolution prefers variable
    /// values over field-name string-coercion in those positions.
    /// </summary>
    public bool TryResolveFieldReceiverContext(AlToken tok)
    {
        var receiver = _state.CurrentFieldReceiver;
        if (receiver is null) return false;

        var name = tok.Value;
        var key = name.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.ContainsKey(key)) return false;
        }

        var member = _state.Ctx.Resolver.ResolveMember(receiver, name);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return false;

        var targetOwner = member.DeclaringType ?? receiver;
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
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Handles a chain whose head turned out not to be a variable or
    /// a catalog type, but matches a field on the implicit Rec
    /// receiver. Emits a <c>field_access</c> at the head, advances
    /// the receiver to the field's return type (an enum for
    /// <c>"Document Type"</c>, a record for FK-typed fields, etc.),
    /// and continues the chain via
    /// <see cref="WalkMemberChain"/>. Returns true when the head
    /// resolved as a Rec field — the caller is done; false to let
    /// the existing unresolved diagnostic fire.
    /// </summary>
    private bool TryConsumeImplicitRecFieldChainHead(AlToken head)
    {
        var rec = RecType();
        if (rec is null) return false;

        var member = _state.Ctx.Resolver.ResolveMember(rec, head.Value);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return false;

        var targetOwner = member.DeclaringType ?? rec;
        _state.EmitReference(new ExtractedReference(
            Line: head.Line,
            Column: head.Column,
            TargetAppId: targetOwner.AppId,
            TargetObjectKind: targetOwner.Kind,
            TargetObjectId: targetOwner.ObjectId,
            TargetObjectName: targetOwner.Name,
            TargetMemberName: member.Name,
            TargetMemberKind: member.Kind,
            ReferenceKind: "field_access"));
        _state.Resolved++;

        WalkMemberChain(AdvanceReceiverByMember(member));
        return true;
    }

    /// <summary>
    /// Resolves the head identifier of a member chain. Returns the
    /// receiver type on success; null when neither the variable
    /// lookup nor the catalog lookup found a type.
    /// <paramref name="declaredAsVar"/> reports the in-scope declaration
    /// for diagnostic purposes when the catalog lookup of a declared
    /// variable's type fails — letting the caller distinguish
    /// "head wasn't a variable at all" from "head was a variable
    /// but its declared type doesn't resolve" (which usually means a
    /// visibility / dependency-graph issue worth surfacing).
    /// </summary>
    private AlTypeRef? ResolveHeadType(AlToken head, out ResolvedVariableType? declaredAsVar)
    {
        declaredAsVar = null;
        // Step 1: walk the scope stack innermost-first.
        var name = head.Value.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.TryGetValue(name, out var declared))
            {
                declaredAsVar = declared;
                // Variables typed with an AL object keyword
                // (Record Customer, Codeunit "Sales-Post") resolve
                // to a type in the catalog. Variables typed with a
                // bare identifier (HttpClient, JsonObject) don't.
                // Pass the keyword through as a kind hint — `Record`
                // means "table", `Codeunit` means "codeunit", etc.
                // Disambiguates name collisions across kinds.
                if (string.IsNullOrEmpty(declared.TypeName)) return null;
                return _state.Ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
            }
        }

        // Step 2: head IS a type name (Customer.Insert pattern).
        return _state.Ctx.Resolver.ResolveTypeByName(head.Value);
    }

    private AlTypeRef? AdvanceReceiverByMember(AlMember member)
    {
        // Fields with a Record-keyword type continue the chain;
        // scalar types (Code, Text, Decimal, etc.) terminate it.
        // Procedures with a known return type continue the chain;
        // procedures with no return / scalar return terminate.
        if (string.IsNullOrEmpty(member.ReturnTypeName)) return null;
        // Only AL-typed returns (Record / Codeunit / Page / …)
        // resolve to catalog entries. System types come back null.
        return _state.Ctx.Resolver.ResolveTypeByName(member.ReturnTypeName);
    }

    private void AdvancePastChain()
    {
        // We've already advanced past the head. Walk through any
        // `.member` follow-ons and method-call argument lists so the
        // main loop doesn't re-enter and double-count.
        while (_state.Pos < _state.Tokens.Count && _state.At("."))
        {
            _state.Pos++; // .
            if (_state.Pos < _state.Tokens.Count
                && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                    || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                _state.Pos++;
            }
            if (_state.Pos < _state.Tokens.Count && _state.At("(")) _state.SkipBalancedParens();
        }
    }

    private void AdvancePastRemainingChain() => AdvancePastChain();

    /// <summary>
    /// Emits a <c>field_access</c> reference at <paramref name="tok"/>
    /// when its value resolves to a field on <paramref name="receiver"/>.
    /// Silent no-op otherwise so callers can scan permissively. Used
    /// by the orchestrator's object-scope property handlers
    /// (DataCaptionFields, CalcFormula, SourceTableView's where/sorting
    /// clauses) which all emit field references against a known
    /// receiver type.
    /// </summary>
    public void EmitFieldAccessIfResolves(AlToken tok, AlTypeRef receiver)
    {
        var member = _state.Ctx.Resolver.ResolveMember(receiver, tok.Value);
        if (member is null) return;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return;
        var targetOwner = member.DeclaringType ?? receiver;
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
}
