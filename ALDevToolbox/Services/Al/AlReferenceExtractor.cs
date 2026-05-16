using System;
using System.Collections.Generic;
using System.Linq;

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
            return new AlExtractionResult(Array.Empty<ExtractedReference>(), new ExtractionStats(0, 0));
        }

        var tokens = AlLexer.Tokenize(source);
        var walker = new Walker(tokens, context);
        return walker.Run();
    }

    private sealed class Walker
    {
        private readonly List<AlToken> _tokens;
        private readonly AlExtractContext _ctx;
        private readonly Stack<ScopeFrame> _scopeStack = new();
        private readonly List<ExtractedReference> _refs = new();
        private int _unresolved;
        private int _resolved;
        private int _pos;

        public Walker(List<AlToken> tokens, AlExtractContext ctx)
        {
            _tokens = tokens;
            _ctx = ctx;
        }

        public AlExtractionResult Run()
        {
            // Object-scope is the outermost frame; it holds the global
            // variables the import pipeline already extracted, plus the
            // owner type for Rec / xRec when applicable.
            _scopeStack.Push(BuildGlobalScope());

            while (_pos < _tokens.Count)
            {
                var tok = _tokens[_pos];

                // Procedure / trigger heads start a new scope frame.
                if (IsScopeOpener(tok))
                {
                    StartProcedureScope();
                    continue;
                }

                // A standalone `end;` closes the innermost procedure scope.
                // Object-scope (the bottom frame) is never popped.
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, "end", StringComparison.OrdinalIgnoreCase)
                    && _scopeStack.Count > 1
                    && IsTopLevelEndOfBlock(_pos))
                {
                    _scopeStack.Pop();
                    _pos++;
                    continue;
                }

                // Member-access chain candidates. Two shapes trigger:
                //   A. Identifier `.` Member …
                //   B. Identifier `::` QuotedIdentifier (or Identifier)
                //      `.` Member …  — the typed-literal head pattern
                //      `Codeunit::"Sales-Post".Run(SalesHeader)` etc.
                // Anything else just advances past.
                if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    if (_pos + 1 < _tokens.Count
                        && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                        && _tokens[_pos + 1].Value == ".")
                    {
                        TryConsumeMemberChain();
                        continue;
                    }

                    if (_pos + 2 < _tokens.Count
                        && _tokens[_pos + 1].Kind == AlTokenKind.DoubleColon
                        && (_tokens[_pos + 2].Kind == AlTokenKind.Identifier
                            || _tokens[_pos + 2].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        TryConsumeTypedLiteralChain();
                        continue;
                    }

                    // Bare self-procedure call: `Identifier(` with no
                    // receiver. We only try the self-member lookup for
                    // unquoted identifiers — quoted identifiers as bare
                    // calls don't occur in practice (procedure names aren't
                    // quoted at call sites in BC).
                    if (tok.Kind == AlTokenKind.Identifier
                        && _pos + 1 < _tokens.Count
                        && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                        && _tokens[_pos + 1].Value == "(")
                    {
                        if (TryConsumeBareSelfCall()) continue;
                    }
                }

                _pos++;
            }

            return new AlExtractionResult(_refs, new ExtractionStats(_resolved, _unresolved));
        }

        // ── Scope frames ──────────────────────────────────────────────

        private ScopeFrame BuildGlobalScope()
        {
            var frame = new ScopeFrame();
            foreach (var (name, type) in _ctx.GlobalVars)
            {
                frame.Vars[name] = type;
            }
            // Tables, pages, table extensions and page extensions expose
            // implicit Rec / xRec self-references. The receiver type
            // differs by owner kind:
            //   - table / tableextension → Rec is the table itself
            //     (or the extended base table; same name for extensions
            //     since AL flattens fields).
            //   - page / pageextension → Rec is the page's SourceTable.
            //     Without that, Rec.X looks for field X on the page,
            //     which doesn't have fields. The importer denormalises
            //     SourceTable onto the owner object so we can find it
            //     here without a catalog lookup.
            //   - report / reportextension / xmlport / query / requestpage
            //     also expose Rec/xRec, but the source-table binding is
            //     more varied (per-dataitem); v1 keeps the owner-itself
            //     fallback there.
            if (OwnerSupportsRecXRec())
            {
                ResolvedVariableType selfType;
                if ((string.Equals(_ctx.OwnerKind, "page", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(_ctx.OwnerKind, "pageextension", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrEmpty(_ctx.OwnerSourceTableName))
                {
                    selfType = new ResolvedVariableType("Record", _ctx.OwnerSourceTableName);
                }
                else
                {
                    selfType = new ResolvedVariableType(_ctx.OwnerKind, _ctx.OwnerName);
                }
                frame.Vars["rec"] = selfType;
                frame.Vars["xrec"] = selfType;
                // CurrFieldRef in field validate triggers, etc., is a
                // FieldRef — not a record — so we don't add it here.
            }
            return frame;
        }

        private bool OwnerSupportsRecXRec()
        {
            var k = _ctx.OwnerKind?.ToLowerInvariant();
            return k == "table" || k == "tableextension"
                || k == "page" || k == "pageextension"
                || k == "report" || k == "reportextension"
                || k == "xmlport" || k == "query"
                || k == "requestpage";
        }

        private static bool IsScopeOpener(AlToken tok)
        {
            if (tok.Kind != AlTokenKind.Identifier) return false;
            var v = tok.Value;
            return string.Equals(v, "procedure", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "trigger", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Heuristic: an `end` token closes a procedure scope when it's
        /// followed by `;` and the immediately preceding context isn't
        /// nested inside another begin block. AL's begin/end pairs
        /// nest cleanly, so we maintain a begin-depth counter as we walk
        /// the body. The procedure scope's frame popping aligns with the
        /// `end;` that closes the procedure body (the outermost begin
        /// inside the procedure).
        /// </summary>
        private bool IsTopLevelEndOfBlock(int endTokenIndex)
        {
            // The current scope frame tracks how many `begin` keywords
            // are open inside it. The closing `end;` we care about is
            // the one that drains the body to zero.
            return _scopeStack.Peek().BeginDepth == 1;
        }

        /// <summary>
        /// Starts a fresh scope frame for a `procedure` or `trigger`
        /// declaration. Parses the parameter list and optional var block,
        /// then positions the cursor immediately after the opening
        /// `begin` so the main loop walks the body with the new frame
        /// active.
        /// </summary>
        private void StartProcedureScope()
        {
            // Skip past `procedure` / `trigger` keyword.
            _pos++;

            // Skip scope keyword if procedure is annotated (local /
            // internal / protected). Actually the keyword sits BEFORE
            // `procedure`, so we don't expect to see it here; included
            // defensively for variant grammars.
            SkipWhitespaceTokens();

            // Procedure / trigger name — for triggers like `OnValidate`
            // this is just an identifier; for procedures it's the
            // procedure name. We don't need the name here; the scope
            // frame is positional.
            if (_pos < _tokens.Count
                && (_tokens[_pos].Kind == AlTokenKind.Identifier
                    || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                _pos++;
            }

            var frame = new ScopeFrame();

            // Optional parameter list: `(name: Type; name: Type)`.
            SkipWhitespaceTokens();
            if (At("(")) ParseParameterList(frame);

            // Optional return-type clause: `: ReturnType` — no variables
            // introduced, just skip until we hit `var` or `begin`.

            // Optional local var block: `var name: Type; ...` between
            // procedure head and `begin`.
            while (_pos < _tokens.Count
                && !(IsIdentifierTok(_pos, "begin") || IsIdentifierTok(_pos, "var")))
            {
                _pos++;
            }
            if (IsIdentifierTok(_pos, "var"))
            {
                _pos++;
                ParseVarBlock(frame);
            }

            // Skip to and past `begin`.
            while (_pos < _tokens.Count && !IsIdentifierTok(_pos, "begin"))
            {
                _pos++;
            }
            if (IsIdentifierTok(_pos, "begin"))
            {
                _pos++;
                frame.BeginDepth = 1;
            }

            _scopeStack.Push(frame);
        }

        private void ParseParameterList(ScopeFrame frame)
        {
            // Expect `(`. Walk until matching `)`. Each parameter is
            // [var] name [, name]* : Type [; ...].
            if (!At("(")) return;
            _pos++; // consume (

            while (_pos < _tokens.Count && !At(")"))
            {
                // Optional `var` modifier on a parameter — pass by ref.
                if (IsIdentifierTok(_pos, "var")) _pos++;

                // One or more comma-separated parameter names sharing a type.
                var names = new List<string>();
                while (_pos < _tokens.Count
                       && (_tokens[_pos].Kind == AlTokenKind.Identifier
                           || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    names.Add(_tokens[_pos].Value);
                    _pos++;
                    if (At(",")) { _pos++; continue; }
                    break;
                }

                // Expect `:` then Type.
                if (!At(":")) { SkipToNextParam(); continue; }
                _pos++;

                var type = ReadTypeReference();
                foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

                // Step past `;` if present.
                if (At(";")) _pos++;
            }
            if (At(")")) _pos++;
        }

        private void SkipToNextParam()
        {
            while (_pos < _tokens.Count && !At(";") && !At(")")) _pos++;
            if (At(";")) _pos++;
        }

        private void ParseVarBlock(ScopeFrame frame)
        {
            // Inside `var ... begin`. Each declaration is
            // `name[, name]*: Type;`. Stop at `begin`.
            while (_pos < _tokens.Count && !IsIdentifierTok(_pos, "begin"))
            {
                if (_tokens[_pos].Kind != AlTokenKind.Identifier
                    && _tokens[_pos].Kind != AlTokenKind.QuotedIdentifier)
                {
                    _pos++;
                    continue;
                }

                var names = new List<string>();
                while (_pos < _tokens.Count
                       && (_tokens[_pos].Kind == AlTokenKind.Identifier
                           || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    names.Add(_tokens[_pos].Value);
                    _pos++;
                    if (At(",")) { _pos++; continue; }
                    break;
                }

                if (!At(":")) { SkipToSemicolonOrBegin(); continue; }
                _pos++;

                var type = ReadTypeReference();
                foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

                if (At(";")) _pos++;
            }
        }

        private void SkipToSemicolonOrBegin()
        {
            while (_pos < _tokens.Count
                   && !At(";")
                   && !IsIdentifierTok(_pos, "begin"))
            {
                _pos++;
            }
            if (At(";")) _pos++;
        }

        /// <summary>
        /// Reads a type reference like <c>Codeunit "Sales-Post"</c>,
        /// <c>Record Customer</c>, <c>Page "Customer Card"</c>,
        /// <c>Boolean</c>, <c>Code[20]</c>. Returns the (keyword,
        /// type name) pair the rest of the extractor cares about.
        /// Bare system types like <c>HttpClient</c> come back with a
        /// null Keyword — those won't resolve to AL objects later.
        /// </summary>
        private ResolvedVariableType ReadTypeReference()
        {
            string? keyword = null;
            string typeName = string.Empty;

            if (_pos >= _tokens.Count) return new ResolvedVariableType(null, "");

            // First token is either an AL object keyword (Record /
            // Codeunit / Page / …) followed by the type name, or the
            // type identifier itself.
            var first = _tokens[_pos];
            if (first.Kind == AlTokenKind.Identifier && IsAlObjectKeyword(first.Value))
            {
                keyword = first.Value;
                _pos++;
                if (_pos < _tokens.Count)
                {
                    var t = _tokens[_pos];
                    if (t.Kind == AlTokenKind.Identifier || t.Kind == AlTokenKind.QuotedIdentifier)
                    {
                        typeName = t.Value;
                        _pos++;
                    }
                }
            }
            else if (first.Kind == AlTokenKind.Identifier || first.Kind == AlTokenKind.QuotedIdentifier)
            {
                typeName = first.Value;
                _pos++;
            }

            // Length qualifier on a scalar (Code[20], Text[100]) —
            // we don't care, just skip.
            while (_pos < _tokens.Count && At("["))
            {
                while (_pos < _tokens.Count && !At("]")) _pos++;
                if (At("]")) _pos++;
            }

            // Generic type parameters: `of [Type, Type, …]` on List /
            // Dictionary / built-in generic shapes. The contents don't
            // resolve to AL objects so the extractor never reads them,
            // but if we don't consume the `of [...]` tail here, the
            // var-block parser walks back into it and treats `of` as
            // the next variable name — which silently drops the
            // variable declared AFTER the generic-typed one. Bracket
            // depth tracking so `[Code[20], Integer]` nests cleanly.
            if (IsIdentifierTok(_pos, "of"))
            {
                _pos++; // of
                if (At("["))
                {
                    int depth = 0;
                    do
                    {
                        if (At("[")) depth++;
                        else if (At("]")) depth--;
                        _pos++;
                    } while (_pos < _tokens.Count && depth > 0);
                }
            }

            return new ResolvedVariableType(keyword, typeName);
        }

        // ── Member-chain extraction ───────────────────────────────────

        /// <summary>
        /// Reads a chain of <c>head.member.member…</c> starting at the
        /// current token, resolves the type at each step, and emits one
        /// reference per <c>.member</c> we manage to resolve. The cursor
        /// advances past the entire chain when we return.
        /// </summary>
        private void TryConsumeMemberChain()
        {
            // We arrive with _tokens[_pos] = head identifier, [_pos+1] = ".".
            var head = _tokens[_pos];
            _pos++;

            // Resolve the head to a type (a starting receiver). Two shapes:
            //   1. head matches a variable in any enclosing scope frame.
            //   2. head matches an object name in the type catalog (the
            //      `Customer.Insert(true)` pattern).
            // Anything else: this chain doesn't yield references.
            var receiverType = ResolveHeadType(head);
            if (receiverType is null)
            {
                _unresolved++;
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
        private void TryConsumeTypedLiteralChain()
        {
            // We arrive with _tokens[_pos] = Kind identifier,
            // [_pos+1] = `::`, [_pos+2] = Name (quoted or bare).
            _pos++; // Kind
            _pos++; // ::
            var nameTok = _tokens[_pos];
            _pos++; // Name

            // The Kind token isn't checked against IsAlObjectKeyword on
            // purpose — the resolver is keyed by Name only, and the
            // grammar lets `Codeunit::`, `Page::`, `Report::` etc. all
            // through the same resolution path. If the resolver finds
            // the name, we have a receiver; otherwise drop the chain.
            var receiverType = _ctx.Resolver.ResolveTypeByName(nameTok.Value);
            if (receiverType is null)
            {
                _unresolved++;
                AdvancePastChain();
                return;
            }

            // Now walk `.member.member…` from the typed receiver.
            // A bare `Codeunit::"Sales-Post"` with no following `.` is
            // a typed value (e.g. passed as a parameter). No reference
            // to emit — we still consumed the head.
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
            while (receiverType is not null && _pos < _tokens.Count && At("."))
            {
                _pos++; // .

                if (_pos >= _tokens.Count) break;
                var memberTok = _tokens[_pos];
                if (memberTok.Kind != AlTokenKind.Identifier
                    && memberTok.Kind != AlTokenKind.QuotedIdentifier)
                {
                    break;
                }
                _pos++;

                // Followed by ( → method_call. Anything else → field_access.
                var followedByParen = _pos < _tokens.Count && At("(");
                var refKind = followedByParen ? "method_call" : "field_access";

                // Resolve member on receiver type.
                var member = _ctx.Resolver.ResolveMember(receiverType, memberTok.Value);
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
                        if (followedByParen) SkipBalancedParens();
                        return;
                    }
                    _unresolved++;
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
                _refs.Add(new ExtractedReference(
                    Line: memberTok.Line,
                    Column: memberTok.Column,
                    TargetAppId: targetOwner.AppId,
                    TargetObjectKind: targetOwner.Kind,
                    TargetObjectId: targetOwner.ObjectId,
                    TargetObjectName: targetOwner.Name,
                    TargetMemberName: member.Name,
                    TargetMemberKind: member.Kind,
                    ReferenceKind: refKind));
                _resolved++;

                // For chained access, advance receiverType to the
                // member's result type (return type for a procedure,
                // declared type for a field). Stop the chain when the
                // member yields a non-record/non-object type or when
                // we can't resolve the next type.
                receiverType = AdvanceReceiverByMember(member);

                // Skip past the method call's argument list so we don't
                // mis-read the arguments as the continuation of the
                // chain. The args themselves get walked by the main
                // loop later — they aren't lost.
                if (followedByParen) SkipBalancedParens();
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
        private bool TryConsumeBareSelfCall()
        {
            var head = _tokens[_pos];
            var name = head.Value;

            if (AlBuiltinMethods.IsStatementKeyword(name))
            {
                // Statement / operator keyword — let the default advance
                // walk past it. Returning false makes the main loop do
                // exactly one _pos++ next iteration; the `(` after it is
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

            // In-scope variable? AL doesn't allow calling a variable as
            // a function — if the name is in scope we treat it as
            // referenced and just advance past.
            var key = name.ToLowerInvariant();
            foreach (var frame in _scopeStack)
            {
                if (frame.Vars.ContainsKey(key))
                {
                    return false;
                }
            }

            // Try to resolve as a member on the file's owner object.
            var ownerType = OwnerType();
            if (ownerType is null) return false;

            var member = _ctx.Resolver.ResolveMember(ownerType, name);
            if (member is null)
            {
                // Could be a bare AL system function we don't have on our
                // list yet, or a same-named procedure on a related extension
                // we don't track from this angle. Counted but not emitted.
                _unresolved++;
                return false;
            }

            // Tableextension-declared self-members fall through the
            // resolver's DeclaringType path same as receiver chains.
            var targetOwner = member.DeclaringType ?? ownerType;
            _refs.Add(new ExtractedReference(
                Line: head.Line,
                Column: head.Column,
                TargetAppId: targetOwner.AppId,
                TargetObjectKind: targetOwner.Kind,
                TargetObjectId: targetOwner.ObjectId,
                TargetObjectName: targetOwner.Name,
                TargetMemberName: member.Name,
                TargetMemberKind: member.Kind,
                ReferenceKind: "method_call"));
            _resolved++;

            // Advance past the identifier only. The argument list is
            // walked by the main loop in its own right — `Outer(Cust.X())`
            // needs the inner `Cust.X()` chain to still be picked up.
            _pos++;
            return true;
        }

        private AlTypeRef? _ownerTypeCache;
        private bool _ownerTypeResolved;

        /// <summary>
        /// Lazily resolves the file owner's <see cref="AlTypeRef"/> via
        /// the resolver. Cached because bare-self-call detection can fire
        /// many times per file and the resolver lookup walks a dictionary
        /// each call.
        /// </summary>
        private AlTypeRef? OwnerType()
        {
            if (_ownerTypeResolved) return _ownerTypeCache;
            _ownerTypeResolved = true;
            if (string.IsNullOrEmpty(_ctx.OwnerName)) return null;
            _ownerTypeCache = _ctx.Resolver.ResolveTypeByName(_ctx.OwnerName);
            return _ownerTypeCache;
        }

        private AlTypeRef? ResolveHeadType(AlToken head)
        {
            // Step 1: walk the scope stack innermost-first.
            var name = head.Value.ToLowerInvariant();
            foreach (var frame in _scopeStack)
            {
                if (frame.Vars.TryGetValue(name, out var declared))
                {
                    // Variables typed with an AL object keyword
                    // (Record Customer, Codeunit "Sales-Post") resolve
                    // to a type in the catalog. Variables typed with a
                    // bare identifier (HttpClient, JsonObject) don't.
                    if (string.IsNullOrEmpty(declared.TypeName)) return null;
                    return _ctx.Resolver.ResolveTypeByName(declared.TypeName);
                }
            }

            // Step 2: head IS a type name (Customer.Insert pattern).
            return _ctx.Resolver.ResolveTypeByName(head.Value);
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
            return _ctx.Resolver.ResolveTypeByName(member.ReturnTypeName);
        }

        private void AdvancePastChain()
        {
            // We've already advanced past the head. Walk through any
            // `.member` follow-ons and method-call argument lists so the
            // main loop doesn't re-enter and double-count.
            while (_pos < _tokens.Count && At("."))
            {
                _pos++; // .
                if (_pos < _tokens.Count
                    && (_tokens[_pos].Kind == AlTokenKind.Identifier
                        || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _pos++;
                }
                if (_pos < _tokens.Count && At("(")) SkipBalancedParens();
            }
        }

        private void AdvancePastRemainingChain() => AdvancePastChain();

        private void SkipBalancedParens()
        {
            if (!At("(")) return;
            int depth = 0;
            do
            {
                if (At("(")) depth++;
                else if (At(")")) depth--;
                _pos++;
            } while (_pos < _tokens.Count && depth > 0);
        }

        // ── Token utilities ───────────────────────────────────────────

        private bool At(string punct) =>
            _pos < _tokens.Count
            && _tokens[_pos].Kind == AlTokenKind.Punct
            && _tokens[_pos].Value == punct;

        private bool IsIdentifierTok(int idx, string name) =>
            idx < _tokens.Count
            && _tokens[idx].Kind == AlTokenKind.Identifier
            && string.Equals(_tokens[idx].Value, name, StringComparison.OrdinalIgnoreCase);

        private void SkipWhitespaceTokens()
        {
            // The lexer already discards whitespace, but Directive tokens
            // (#pragma) sit in the middle of declarations sometimes.
            while (_pos < _tokens.Count && _tokens[_pos].Kind == AlTokenKind.Directive) _pos++;
        }

        private static bool IsAlObjectKeyword(string s) =>
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

    private sealed class ScopeFrame
    {
        public Dictionary<string, ResolvedVariableType> Vars { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tracks how many <c>begin</c> blocks are currently open inside
        /// this procedure's body. The frame pops when the depth drops
        /// to zero (i.e. the matching `end;` of the body).
        /// </summary>
        public int BeginDepth { get; set; }
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
    string? OwnerSourceTableName = null);

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
    /// </summary>
    AlTypeRef? ResolveTypeByName(string typeName);

    /// <summary>
    /// Resolves a member on a known owner. The owner is identified by
    /// its triplet; the member is matched by name (case-insensitive).
    /// When multiple symbols share the name (overloads), implementations
    /// should pick a stable one (typically the first declared) — the
    /// reference row records the name + kind, not a specific overload.
    /// </summary>
    AlMember? ResolveMember(AlTypeRef owner, string memberName);
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
    string TargetMemberName,
    string TargetMemberKind,
    string ReferenceKind);

/// <summary>Per-file extraction statistics — used for diagnostic logging.</summary>
public sealed record ExtractionStats(int ResolvedReferences, int UnresolvedReceivers);

/// <summary>Result envelope: extracted rows plus the run's stats.</summary>
public sealed record AlExtractionResult(
    IReadOnlyList<ExtractedReference> References,
    ExtractionStats Stats);
