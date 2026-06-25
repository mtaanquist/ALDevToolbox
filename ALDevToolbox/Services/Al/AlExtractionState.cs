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
    public List<ExtractedSystemReference> SystemRefs { get; } = new();
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

    /// <summary>
    /// Current nesting depth of <see cref="AlProcedureWalker.WalkBalancedParens"/>.
    /// That method dispatches each arg token back through the orchestrator, which
    /// can re-enter chain handling and call <c>WalkBalancedParens</c> again, so
    /// stack depth grows with paren/chain nesting. A hostile <c>.al</c> file with
    /// thousands of nested <c>(((…)))</c> would otherwise overflow the stack and
    /// crash the shared server process during ingest; once this exceeds
    /// <see cref="MaxWalkDepth"/> the walker falls back to the non-recursive
    /// <see cref="SkipBalancedParens"/>. See issue #363.
    /// </summary>
    public int WalkDepth;

    /// <summary>
    /// Recursion ceiling for <see cref="AlProcedureWalker.WalkBalancedParens"/>.
    /// Real BC source nests at most a handful of paren levels deep; 256 is far
    /// above any legitimate expression while still tripping long before the CLR
    /// stack is exhausted.
    /// </summary>
    public const int MaxWalkDepth = 256;

    public AlTypeRef? OwnerTypeCache;
    public bool OwnerTypeResolved;
    public AlTypeRef? RecTypeCache;
    public bool RecTypeResolved;
    public AlTypeRef? BaseObjectTypeCache;
    public bool BaseObjectTypeResolved;

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

    /// <summary>
    /// Active dataitem / tableelement source tables for kinds that
    /// thread one (report / reportextension / query / xmlport).
    /// Pushed by the per-kind structure extractor when it consumes a
    /// <c>dataitem(alias; SourceTable)</c> /
    /// <c>tableelement(alias; SourceTable)</c> declaration; the entry
    /// records the <see cref="ObjectBraceDepth"/> at which it was
    /// pushed so <see cref="OnObjectBraceClose"/> can pop it when the
    /// matching body brace closes. Innermost dataitem is at the top.
    ///
    /// Walked by <see cref="AlProcedureWalker.TryConsumeBareSelfCall"/>
    /// from innermost to outermost so a bare call in a NESTED
    /// dataitem trigger (e.g. <c>EmptyLine()</c> inside an
    /// <c>Integer</c>-loop dataitem under a <c>"Gen. Journal Line"</c>
    /// parent) still resolves against the parent's source table when
    /// the inner one doesn't expose the method. Single-dataitem
    /// reports get the same behaviour as before (only one frame on
    /// the stack).
    /// </summary>
    public readonly List<DataItemFrame> DataItemStack = new();

    /// <summary>
    /// Innermost dataitem source (top of <see cref="DataItemStack"/>),
    /// or null when no dataitem is active. Preserved as a property
    /// because object-scope helpers (column source-field resolution,
    /// implicit-Rec field access fallback) only ever want the
    /// innermost.
    /// </summary>
    public AlTypeRef? CurrentDataItemSource =>
        DataItemStack.Count > 0 ? DataItemStack[^1].Source : null;

    /// <summary>
    /// Object-scope brace depth counter, incremented on every <c>{</c>
    /// and decremented on every <c>}</c> while the walker is outside
    /// any procedure / trigger body. Used to scope
    /// <see cref="DataItemStack"/> entries to their dataitem's body.
    /// Resets to 0 at the start of each file.
    /// </summary>
    public int ObjectBraceDepth;

    /// <summary>
    /// Push a dataitem / tableelement source onto the stack, recording
    /// the current <see cref="ObjectBraceDepth"/> so the matching
    /// body-closing brace pops it. Call from the per-kind structure
    /// extractor at the moment the declaration is consumed (before the
    /// body's opening <c>{</c> is processed).
    /// </summary>
    public void PushDataItemSource(AlTypeRef source)
    {
        DataItemStack.Add(new DataItemFrame(ObjectBraceDepth, source));
        // Rec shadows over the active dataitem; bust the cache so a
        // RecType() call inside this dataitem's body re-reads from the
        // updated stack instead of returning the outer binding.
        RecTypeResolved = false;
        RecTypeCache = null;
    }

    /// <summary>
    /// Called from the orchestrator when a <c>}</c> is encountered at
    /// object scope. Decrements <see cref="ObjectBraceDepth"/> and
    /// pops any dataitem frames whose recorded depth is now at or
    /// above the current depth — they've left their body.
    /// </summary>
    public void OnObjectBraceClose()
    {
        if (ObjectBraceDepth > 0) ObjectBraceDepth--;
        bool popped = false;
        while (DataItemStack.Count > 0
               && DataItemStack[^1].Depth >= ObjectBraceDepth)
        {
            DataItemStack.RemoveAt(DataItemStack.Count - 1);
            popped = true;
        }
        if (popped)
        {
            RecTypeResolved = false;
            RecTypeCache = null;
        }
    }

    /// <summary>
    /// Iterate the active dataitem sources from innermost to outermost.
    /// Bare-call / implicit-field-access resolvers consult this to
    /// resolve names that live on a parent dataitem's source table
    /// when the innermost one doesn't expose them.
    /// </summary>
    public IEnumerable<AlTypeRef> ActiveDataItemSources()
    {
        for (int i = DataItemStack.Count - 1; i >= 0; i--)
        {
            yield return DataItemStack[i].Source;
        }
    }

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
    /// Emits a resolved member/object reference and bumps <see cref="Resolved"/>
    /// in one step. Every resolved-reference site in the walker has the same
    /// shape — build an <see cref="ExtractedReference"/> from a target
    /// <see cref="AlTypeRef"/> plus an optional member, then increment the
    /// resolved counter — so this folds the two-line dance into one call and
    /// keeps the field mapping in a single place.
    /// </summary>
    /// <summary>
    /// True when <paramref name="lowerKey"/> (an already-lower-cased variable
    /// name) is declared in any frame on the scope stack. Callers use it to
    /// skip a token that's really a local/parameter rather than a member or
    /// catalog name.
    /// </summary>
    public bool IsVarInScope(string lowerKey)
    {
        foreach (var frame in ScopeStack)
        {
            if (frame.Vars.ContainsKey(lowerKey)) return true;
        }
        return false;
    }

    public void EmitResolved(
        int line,
        int column,
        AlTypeRef target,
        string? memberName,
        string? memberKind,
        string referenceKind)
    {
        EmitReference(new ExtractedReference(
            Line: line,
            Column: column,
            TargetAppId: target.AppId,
            TargetObjectKind: target.Kind,
            TargetObjectId: target.ObjectId,
            TargetObjectName: target.Name,
            TargetMemberName: memberName,
            TargetMemberKind: memberKind,
            ReferenceKind: referenceKind));
        Resolved++;
    }

    /// <summary>
    /// Records a built-in / system method call (the rows the normal extractor
    /// drops). Stamps the enclosing procedure/trigger from the scope frame the
    /// same way <see cref="EmitReference"/> does, so the panel can show which
    /// member each system call sits in. See issue #279.
    /// </summary>
    public void EmitSystemReference(ExtractedSystemReference reference)
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
        SystemRefs.Add(reference);
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
/// One entry on <see cref="AlExtractionState.DataItemStack"/>. Records
/// the <see cref="Depth"/> (<see cref="AlExtractionState.ObjectBraceDepth"/>
/// at push time) so the matching body-closing brace can pop it.
/// </summary>
internal readonly record struct DataItemFrame(int Depth, AlTypeRef Source);

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

    /// <summary>
    /// True when this frame was pushed by a <c>with X do begin … end</c>
    /// statement (deprecated AL but still used in Microsoft's legacy
    /// regional/banking modules). The frame's <c>Vars["rec"]</c> overrides
    /// Rec for the duration of the block so bare identifiers resolve to
    /// fields/procedures on <c>X</c>'s record type. <see cref="AlProcedureWalker.TryHandleBlockDepth"/>
    /// uses this flag to invalidate the cached <see cref="AlExtractionState.RecTypeCache"/>
    /// when the frame pops.
    /// </summary>
    public bool IsWithFrame { get; set; }

    /// <summary>
    /// True for the single-statement <c>with X do &lt;stmt&gt;;</c> form
    /// — no <c>begin</c>/<c>end</c> anchor, so the frame can't be
    /// popped through the normal begin/end pairing. Instead the frame
    /// records <see cref="EndTokenIndex"/> at push time (computed by a
    /// forward scan from <c>do</c> through to the statement's matching
    /// <c>;</c>) and the orchestrator pops it the moment the cursor
    /// reaches that position. Without this, the canonical BC pattern
    /// <c>WITH GenJournalLine DO IF X THEN InsertPaymentFileError(...)</c>
    /// loses the Rec rebind and the bare call fires unresolved.
    /// </summary>
    public bool IsSingleStmtWith { get; set; }

    /// <summary>
    /// Token index ONE PAST the closing <c>;</c> of a single-statement
    /// <c>with X do</c> body. Set when <see cref="IsSingleStmtWith"/>
    /// is true; ignored otherwise. The orchestrator pops the frame
    /// when <c>state.Pos &gt;= EndTokenIndex</c>.
    /// </summary>
    public int EndTokenIndex { get; set; }
}
