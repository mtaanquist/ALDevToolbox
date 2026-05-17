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
            return new AlExtractionResult(
                Array.Empty<ExtractedReference>(),
                new ExtractionStats(0, 0, Array.Empty<UnresolvedSample>()));
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
        private readonly List<UnresolvedSample> _unresolvedSamples = new();
        private int _unresolved;
        private int _resolved;
        private int _pos;

        // Cap per-file samples so a pathological file (machine-generated,
        // dependency-on-an-uningested-app) doesn't balloon memory. The
        // import pipeline reservoir-merges a smaller global cap on top
        // of these.
        private const int UnresolvedSampleCap = 20;

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
                ProcessOneToken();
            }

            return new AlExtractionResult(
                _refs,
                new ExtractionStats(_resolved, _unresolved, _unresolvedSamples));
        }

        /// <summary>
        /// Dispatches a single token through the same matchers Run() uses.
        /// Extracted so <see cref="WalkBalancedParens"/> can reuse the
        /// same dispatch when walking inside a method call's argument
        /// list, ensuring references inside <c>Rec.Validate("X", Y.Z)</c>
        /// emit instead of being swallowed by a SkipBalancedParens.
        /// Advances <c>_pos</c> by at least one position each call.
        /// </summary>
        private void ProcessOneToken()
        {
            var tok = _tokens[_pos];

            // Modern AL files open with `namespace X.Y.Z;` and a sequence
            // of `using A.B.C;` directives. The chain walker would otherwise
            // treat each dotted name as a member chain on an unresolved
            // head — emitting one false unresolved per directive (~17 per
            // BC file × 17k files in a release). Consume the whole directive
            // up to the next `;` instead.
            if (_scopeStack.Count == 1
                && tok.Kind == AlTokenKind.Identifier
                && (string.Equals(tok.Value, "namespace", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tok.Value, "using", StringComparison.OrdinalIgnoreCase)))
            {
                SkipToSemicolonAtTopLevel();
                return;
            }

            // Procedure / trigger heads start a new scope frame.
            if (IsScopeOpener(tok))
            {
                StartProcedureScope();
                return;
            }

            // Object-scope `var` keyword: scan label declarations
            // (`Name: Label '…';`) into the bottom frame so bare uses
            // of label vars in procedure bodies resolve through the
            // same scope-walking path field accesses do.
            if (_scopeStack.Count == 1
                && tok.Kind == AlTokenKind.Identifier
                && string.Equals(tok.Value, "var", StringComparison.OrdinalIgnoreCase))
            {
                ScanObjectScopeLabels();
                return;
            }

            // Inside a procedure / trigger body, track nested block
            // depth. `begin` and `case` open blocks closed by `end`;
            // we maintain the depth so the procedure's own `end;`
            // pops the scope frame, not any nested `end`.
            if (_scopeStack.Count > 1 && tok.Kind == AlTokenKind.Identifier)
            {
                if (string.Equals(tok.Value, "begin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tok.Value, "case", StringComparison.OrdinalIgnoreCase))
                {
                    _scopeStack.Peek().BeginDepth++;
                    _pos++;
                    return;
                }
                if (string.Equals(tok.Value, "end", StringComparison.OrdinalIgnoreCase))
                {
                    var frame = _scopeStack.Peek();
                    frame.BeginDepth--;
                    if (frame.BeginDepth <= 0)
                    {
                        _scopeStack.Pop();
                    }
                    _pos++;
                    return;
                }
            }

            // Member-access chain candidates. Two shapes trigger:
            //   A. Identifier `.` Member …
            //   B. Identifier `::` QuotedIdentifier (or Identifier)
            //      `.` Member …  — the typed-literal head pattern.
            if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
            {
                if (_pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == ".")
                {
                    TryConsumeMemberChain();
                    return;
                }

                if (_pos + 2 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.DoubleColon
                    && (_tokens[_pos + 2].Kind == AlTokenKind.Identifier
                        || _tokens[_pos + 2].Kind == AlTokenKind.QuotedIdentifier))
                {
                    TryConsumeTypedLiteralChain();
                    return;
                }

                // Bare self-procedure call: `Identifier(` with no receiver.
                if (tok.Kind == AlTokenKind.Identifier
                    && _pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == "(")
                {
                    if (TryConsumeBareSelfCall()) return;
                }

                // Bare use of an in-scope Label-typed variable.
                if (_scopeStack.Count > 1
                    && (tok.Kind == AlTokenKind.QuotedIdentifier || tok.Kind == AlTokenKind.Identifier))
                {
                    if (TryConsumeLabelUse()) return;
                }

                // Implicit-Rec field access.
                if (_scopeStack.Count > 1
                    && (tok.Kind == AlTokenKind.QuotedIdentifier || tok.Kind == AlTokenKind.Identifier))
                {
                    if (TryConsumeImplicitFieldAccess()) return;
                }

                // Object-scope property: `Identifier = Value;`.
                if (_scopeStack.Count == 1
                    && tok.Kind == AlTokenKind.Identifier
                    && _pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == "=")
                {
                    if (TryConsumeObjectScopeProperty()) return;
                }

                // Field declaration with a typed third arg.
                if (_scopeStack.Count == 1
                    && tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, "field", StringComparison.OrdinalIgnoreCase)
                    && _pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == "(")
                {
                    TryConsumeFieldDeclaration();
                    return;
                }

                // Other page / table / report DSL keywords at object scope
                // with a control-name first arg: `part(ControlName; Page)`,
                // `action(ControlName)`, `actionref(ControlName; Target)`,
                // `group(Name)`, `area(Name)`, `repeater(Name)`,
                // `cuegroup(Name)`, `modify(Name)`, `addafter(Name)`,
                // `dataitem(Name; Source)`, `value(N; Name)` etc. The
                // bare-call resolver already silences the keyword itself;
                // here we additionally skip past the FIRST argument so it
                // doesn't get mis-emitted as an implicit-Rec field access
                // or an unresolved chain head. The second arg (source
                // expression / page-object reference / table reference)
                // continues to walk through the main dispatch so its
                // references still emit. `field` has its own dispatch
                // above so the table-side `(N; "Name"; Type)` shape can
                // extract the type reference; this branch only catches
                // the non-field DSL keywords.
                if (_scopeStack.Count == 1
                    && tok.Kind == AlTokenKind.Identifier
                    && _pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == "("
                    && AlBuiltinMethods.IsObjectDslKeyword(tok.Value))
                {
                    _pos++; // the DSL keyword itself
                    SkipDslKeywordFirstArg();
                    return;
                }
            }

            // EventSubscriber attribute detection.
            if (_scopeStack.Count == 1
                && tok.Kind == AlTokenKind.Punct && tok.Value == "["
                && _pos + 1 < _tokens.Count
                && _tokens[_pos + 1].Kind == AlTokenKind.Identifier
                && string.Equals(_tokens[_pos + 1].Value, "EventSubscriber", StringComparison.OrdinalIgnoreCase))
            {
                TryConsumeEventSubscriberAttribute();
                return;
            }

            _pos++;
        }

        /// <summary>
        /// Walks tokens inside a balanced <c>( … )</c> range, dispatching
        /// each through <see cref="ProcessOneToken"/> so references
        /// embedded in method arguments emit naturally. Called by
        /// <see cref="WalkMemberChain"/> after emitting a method-call
        /// reference, replacing the previous <c>SkipBalancedParens</c>
        /// that swallowed every arg-side reference (so
        /// <c>Rec.Validate("Sell-to Customer No.", Customer."No.")</c>
        /// was losing both arg references).
        ///
        /// Assumes <c>_pos</c> is at the opening <c>(</c>. On return,
        /// <c>_pos</c> is past the matching <c>)</c>.
        /// </summary>
        private void WalkBalancedParens()
        {
            if (!At("(")) return;
            _pos++; // past `(`
            int depth = 1;
            while (_pos < _tokens.Count)
            {
                if (At("("))
                {
                    depth++;
                    _pos++;
                    continue;
                }
                if (At(")"))
                {
                    depth--;
                    _pos++;
                    if (depth == 0) return;
                    continue;
                }
                ProcessOneToken();
            }
        }

        // ── Scope frames ──────────────────────────────────────────────

        private ScopeFrame BuildGlobalScope()
        {
            var frame = new ScopeFrame();
            foreach (var (name, type) in _ctx.GlobalVars)
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
            return frame;
        }

        private ResolvedVariableType? DetermineRecBinding()
        {
            var k = _ctx.OwnerKind?.ToLowerInvariant();

            // Codeunit: Rec only exists when TableNo binds it. Without
            // the binding, a codeunit has no Rec — most non-trigger
            // codeunits fall here, so returning null is correct.
            if (k == "codeunit")
            {
                if (string.IsNullOrEmpty(_ctx.OwnerSourceTableName)) return null;
                return new ResolvedVariableType("Record", _ctx.OwnerSourceTableName);
            }

            // Pages and tableextensions: Rec is the SourceTable / base.
            // Tableextension's SourceTable is set from ExtendsObjectName
            // at import time so the extension's Rec.<X> resolves through
            // the base table's catalog + extensions.
            if (k == "page" || k == "pageextension" || k == "tableextension")
            {
                if (string.IsNullOrEmpty(_ctx.OwnerSourceTableName)) return null;
                return new ResolvedVariableType("Record", _ctx.OwnerSourceTableName);
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
                return new ResolvedVariableType(_ctx.OwnerKind!, _ctx.OwnerName);
            }

            return null;
        }

        private static bool IsScopeOpener(AlToken tok)
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
                        typeNameLine = t.Line;
                        typeNameColumn = t.Column;
                        sawTypeName = true;
                        _pos++;
                    }
                }
            }
            else if (first.Kind == AlTokenKind.Identifier || first.Kind == AlTokenKind.QuotedIdentifier)
            {
                typeName = first.Value;
                _pos++;
            }

            // Only emit when we have an explicit AL keyword. Bare
            // identifier types (Integer, Boolean, custom variables in
            // scope) aren't navigable AL objects and would either
            // mis-resolve or pollute the underline.
            if (keyword is not null && sawTypeName && !string.IsNullOrEmpty(typeName))
            {
                var target = _ctx.Resolver.ResolveTypeByName(typeName, keyword);
                if (target is not null)
                {
                    _refs.Add(new ExtractedReference(
                        Line: typeNameLine,
                        Column: typeNameColumn,
                        TargetAppId: target.AppId,
                        TargetObjectKind: target.Kind,
                        TargetObjectId: target.ObjectId,
                        TargetObjectName: target.Name,
                        TargetMemberName: null,
                        TargetMemberKind: null,
                        ReferenceKind: "property_object"));
                    _resolved++;
                }
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
                while (_pos < _tokens.Count && At("."))
                {
                    _pos++; // .
                    if (_pos < _tokens.Count
                        && (_tokens[_pos].Kind == AlTokenKind.Identifier
                            || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        _pos++; // member
                    }
                    if (_pos < _tokens.Count && At("(")) WalkBalancedParens();
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
                                || IsPlatformVirtualTableId(declaredAsVar.TypeName)))))
                {
                    AdvancePastChain();
                    return;
                }

                _unresolved++;
                // Two sub-cases for the diagnostic samples: did the var
                // lookup miss entirely, or did it find a var whose
                // declared type didn't resolve through the resolver?
                // The latter points at visibility / catalog issues for
                // a known-named type — much more actionable than "name
                // isn't in scope".
                if (declaredAsVar is not null)
                {
                    CaptureUnresolved("head-var-type-unresolved", head, null,
                        receiverNameOverride: $"{declaredAsVar.Keyword ?? "?"} {declaredAsVar.TypeName}");
                }
                else
                {
                    CaptureUnresolved("head-not-a-variable", head, null);
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
        private void TryConsumeTypedLiteralChain()
        {
            // We arrive with _tokens[_pos] = Kind identifier,
            // [_pos+1] = `::`, [_pos+2] = Name (quoted or bare).
            var kindTok = _tokens[_pos];
            _pos++; // Kind
            _pos++; // ::
            var nameTok = _tokens[_pos];
            _pos++; // Name

            // Pass the kind keyword as a hint so name collisions across
            // object kinds disambiguate cleanly — `Codeunit::"Foo"`
            // should never resolve to a Table or Page named "Foo".
            var receiverType = _ctx.Resolver.ResolveTypeByName(nameTok.Value, kindTok.Value);
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
                if (!IsAlObjectKeyword(kindTok.Value)
                    && !string.Equals(kindTok.Value, "DATABASE", StringComparison.OrdinalIgnoreCase))
                {
                    AdvancePastChain();
                    return;
                }
                _unresolved++;
                CaptureUnresolved("typed-literal-name", nameTok, null, kindTok.Value);
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
                        // Built-in like Rec.Validate(...) — no ref to
                        // emit for the call itself, but the args may
                        // contain references that need to surface.
                        // Walk inside the parens via the same dispatch
                        // path the main loop uses.
                        if (followedByParen) WalkBalancedParens();
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
                    _unresolved++;
                    CaptureUnresolved("chain-step", memberTok, receiverType);
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

                // Walk the method call's argument list so references
                // embedded in the args (`Rec.Validate("X", Y.Z)` —
                // both the bare quoted "X" and the chained Y.Z) emit
                // their refs. Was SkipBalancedParens previously, which
                // silently dropped every arg-side reference. _pos
                // lands past the matching `)` on return; the outer
                // while loop's At(".") check then handles any chain
                // continuation that follows.
                if (followedByParen) WalkBalancedParens();
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
                CaptureUnresolved("bare-call", head, ownerType);
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
            // Pass the owner's catalog kind as the hint so bare self-calls
            // on a pageextension named the same as its base page (or any
            // similarly-named extension over its base) land on the
            // extension's own members, not on the base object's. Without
            // the hint, the resolver's non-extension preference would
            // pick the base, which is the wrong receiver for self-calls.
            _ownerTypeCache = _ctx.Resolver.ResolveTypeByName(_ctx.OwnerName, _ctx.OwnerKind);
            return _ownerTypeCache;
        }

        // ── Object-scope property extraction (item 4) ─────────────────

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
            var head = _tokens[_pos];
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
        private bool TryConsumeObjectReferenceProperty(string propertyName)
        {
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            // Optional AL object kind keyword (`Page`, `Codeunit`, etc.).
            string? kindHint = null;
            if (_pos < _tokens.Count && _tokens[_pos].Kind == AlTokenKind.Identifier
                && IsAlObjectKeyword(_tokens[_pos].Value))
            {
                kindHint = _tokens[_pos].Value;
                _pos++;
            }

            if (_pos >= _tokens.Count) { SkipToSemicolon(); return true; }
            var nameTok = _tokens[_pos];
            if (nameTok.Kind != AlTokenKind.Identifier && nameTok.Kind != AlTokenKind.QuotedIdentifier)
            {
                SkipToSemicolon();
                return true;
            }

            var target = _ctx.Resolver.ResolveTypeByName(nameTok.Value);
            if (target is null)
            {
                // Target not in catalog (cross-release, unknown kind, etc.).
                // Don't bump unresolved counter — that bucket is for chain
                // receivers; property misses are common and noisy.
                SkipToSemicolon();
                return true;
            }

            _refs.Add(new ExtractedReference(
                Line: nameTok.Line,
                Column: nameTok.Column,
                TargetAppId: target.AppId,
                TargetObjectKind: target.Kind,
                TargetObjectId: target.ObjectId,
                TargetObjectName: target.Name,
                TargetMemberName: null,
                TargetMemberKind: null,
                ReferenceKind: "property_object"));
            _resolved++;

            // kindHint is intentionally unused at the row level — the
            // catalog lookup already returned the canonical kind.
            _ = kindHint;
            SkipToSemicolon();
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
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            var ownerTable = RecType();
            if (ownerTable is null) { SkipToSemicolon(); return true; }

            while (_pos < _tokens.Count && !At(";"))
            {
                var tok = _tokens[_pos];
                if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    var member = _ctx.Resolver.ResolveMember(ownerTable, tok.Value);
                    if (member is not null
                        && string.Equals(member.Kind, "field", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetOwner = member.DeclaringType ?? ownerTable;
                        _refs.Add(new ExtractedReference(
                            Line: tok.Line,
                            Column: tok.Column,
                            TargetAppId: targetOwner.AppId,
                            TargetObjectKind: targetOwner.Kind,
                            TargetObjectId: targetOwner.ObjectId,
                            TargetObjectName: targetOwner.Name,
                            TargetMemberName: member.Name,
                            TargetMemberKind: member.Kind,
                            ReferenceKind: "field_access"));
                        _resolved++;
                    }
                    _pos++;
                    continue;
                }
                // Skip commas, whitespace tokens we don't care about.
                _pos++;
            }
            if (At(";")) _pos++;
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
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            var seen = new HashSet<long>();
            while (_pos < _tokens.Count && !At(";"))
            {
                var tok = _tokens[_pos];
                if (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier)
                {
                    var target = _ctx.Resolver.ResolveTypeByName(tok.Value);
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
                            _refs.Add(new ExtractedReference(
                                Line: tok.Line,
                                Column: tok.Column,
                                TargetAppId: target.AppId,
                                TargetObjectKind: target.Kind,
                                TargetObjectId: target.ObjectId,
                                TargetObjectName: target.Name,
                                TargetMemberName: null,
                                TargetMemberKind: null,
                                ReferenceKind: "property_object"));
                            _resolved++;
                        }
                    }
                }
                _pos++;
            }
            if (At(";")) _pos++;
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
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            while (_pos < _tokens.Count && !At(";"))
            {
                var tok = _tokens[_pos];
                // Watch for `tabledata <Name>` pairs. Other entry types
                // (e.g. <c>tabledata 27 = rm</c> with a numeric id) miss
                // the catalog lookup and fall through silently.
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, "tabledata", StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    if (_pos >= _tokens.Count) break;
                    var nameTok = _tokens[_pos];
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
                        var target = _ctx.Resolver.ResolveTypeByName(nameTok.Value, "table");
                        if (target is not null
                            && string.Equals(target.Kind, "table", StringComparison.OrdinalIgnoreCase))
                        {
                            _refs.Add(new ExtractedReference(
                                Line: nameTok.Line,
                                Column: nameTok.Column,
                                TargetAppId: target.AppId,
                                TargetObjectKind: target.Kind,
                                TargetObjectId: target.ObjectId,
                                TargetObjectName: target.Name,
                                TargetMemberName: null,
                                TargetMemberKind: null,
                                ReferenceKind: "property_object"));
                            _resolved++;
                        }
                        _pos++;
                    }
                    continue;
                }
                _pos++;
            }
            if (At(";")) _pos++;
            return true;
        }

        /// <summary>
        /// Recognises <c>field(N; "Name"; Type)</c> declarations at
        /// object scope and emits a reference for the type when it's an
        /// AL object (typically <c>Enum "Sales Document Type"</c>).
        ///
        /// Two AL field-declaration forms share the same <c>field(…)</c>
        /// keyword:
        /// <list type="bullet">
        ///   <item><b>Table-side</b>:
        ///         <c>field(&lt;id&gt;; "&lt;name&gt;"; &lt;type&gt;)</c>
        ///         — first arg is a numeric id. The third arg is the
        ///         AL type we want to extract.</item>
        ///   <item><b>Page-side</b>:
        ///         <c>field("&lt;name&gt;"; Rec."&lt;field&gt;")</c>
        ///         — first arg is the page-field name; the second arg
        ///         is a chain expression (typically <c>Rec.&lt;field&gt;</c>)
        ///         that needs the regular member-chain walker to
        ///         resolve. Consuming the parens unconditionally would
        ///         swallow that chain, so we detect the form by peeking
        ///         at the first token and bail out for page-side
        ///         declarations — the main loop walks them naturally.</item>
        /// </list>
        ///
        /// Other type shapes on the table side — <c>Code[20]</c>,
        /// <c>Integer</c>, <c>Decimal</c>, <c>Boolean</c>, <c>DateTime</c>
        /// — aren't AL objects in the catalog, so they fall through
        /// silently after type extraction.
        /// </summary>
        private void TryConsumeFieldDeclaration()
        {
            _pos++; // field
            if (!At("(")) return;
            _pos++; // (

            // Detect form by the first token inside the parens. Number
            // → table-side (id; name; type). Anything else → page-side
            // (controlName; sourceExpression). For page-side we need to
            // walk PAST the controlName + the `;` separator before
            // returning, so the main loop picks up the source expression
            // (Rec."No.", a variable, a function call) and the controlName
            // doesn't get caught by implicit-Rec field-access as a Rec
            // field reference. Without this skip the `field("No."; ...)`
            // form emits a bogus field_access on the page-field's own
            // declared name.
            if (_pos >= _tokens.Count) return;
            if (_tokens[_pos].Kind != AlTokenKind.Number)
            {
                SkipDslKeywordFirstArg(alreadyPastOpenParen: true);
                return;
            }

            // Table-side: three semicolon-separated args at depth 0
            // (id; name; type). We only care about arg[2] — the type.
            int depth = 0;
            int semicolonsSeen = 0;
            var typeTokens = new List<AlToken>();
            while (_pos < _tokens.Count)
            {
                var tok = _tokens[_pos];
                if (tok.Kind == AlTokenKind.Punct)
                {
                    if (tok.Value == "(") { depth++; _pos++; continue; }
                    if (tok.Value == ")")
                    {
                        if (depth == 0) { _pos++; break; }
                        depth--;
                        _pos++;
                        continue;
                    }
                    if (tok.Value == ";" && depth == 0)
                    {
                        semicolonsSeen++;
                        _pos++;
                        continue;
                    }
                }
                if (semicolonsSeen == 2)
                {
                    typeTokens.Add(tok);
                }
                _pos++;
            }

            EmitTypedReference(typeTokens);

            // The declaration's trailing `{ … }` block contains its
            // own properties (Caption, TableRelation, ToolTip, …) — the
            // main loop walks them in their own right.
        }

        /// <summary>
        /// Emits a reference for an AL-object-typed value: when the token
        /// stream looks like <c>&lt;kind&gt; Name</c> (e.g.
        /// <c>Enum "Sales Document Type"</c>, <c>Record Customer</c>),
        /// resolves Name in the catalog and emits a
        /// <c>property_object</c> row. The kind keyword is required so
        /// scalar types like <c>Code[20]</c> don't try to resolve
        /// <c>Code</c> as an object.
        /// </summary>
        private void EmitTypedReference(List<AlToken> tokens)
        {
            if (tokens.Count < 2) return;

            // Find the kind keyword + immediate name pair. Walk forward
            // so `Enum "X"` inside parentheses-stripped tokens still
            // resolves correctly when there's leading / trailing noise.
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                var kw = tokens[i];
                if (kw.Kind != AlTokenKind.Identifier) continue;
                if (!IsAlObjectKeyword(kw.Value)) continue;
                var nameTok = tokens[i + 1];
                if (nameTok.Kind != AlTokenKind.Identifier
                    && nameTok.Kind != AlTokenKind.QuotedIdentifier)
                {
                    continue;
                }
                var target = _ctx.Resolver.ResolveTypeByName(nameTok.Value);
                if (target is null) return;
                _refs.Add(new ExtractedReference(
                    Line: nameTok.Line,
                    Column: nameTok.Column,
                    TargetAppId: target.AppId,
                    TargetObjectKind: target.Kind,
                    TargetObjectId: target.ObjectId,
                    TargetObjectName: target.Name,
                    TargetMemberName: null,
                    TargetMemberKind: null,
                    ReferenceKind: "property_object"));
                _resolved++;
                return;
            }
        }

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
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            // Aggregator function name (sum / count / exist / lookup / …).
            // We don't validate which one — any Identifier followed by
            // `(` is acceptable; the structure inside is what we read.
            if (_pos < _tokens.Count && _tokens[_pos].Kind == AlTokenKind.Identifier)
            {
                _pos++;
            }
            if (!At("(")) { SkipToSemicolon(); return true; }
            _pos++; // (

            // Queried table: bare or quoted identifier as the first token
            // inside the aggregator's parens.
            AlTypeRef? queriedTable = null;
            if (_pos < _tokens.Count
                && (_tokens[_pos].Kind == AlTokenKind.Identifier
                    || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                var tableTok = _tokens[_pos];
                var resolved = _ctx.Resolver.ResolveTypeByName(tableTok.Value);
                if (resolved is not null
                    && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
                {
                    queriedTable = resolved;
                    _refs.Add(new ExtractedReference(
                        Line: tableTok.Line,
                        Column: tableTok.Column,
                        TargetAppId: resolved.AppId,
                        TargetObjectKind: resolved.Kind,
                        TargetObjectId: resolved.ObjectId,
                        TargetObjectName: resolved.Name,
                        TargetMemberName: null,
                        TargetMemberKind: null,
                        ReferenceKind: "property_object"));
                    _resolved++;
                }
                _pos++;
            }

            // Optional `.<field>` immediately after the table name —
            // sum / min / max / average / lookup target a specific field.
            if (queriedTable is not null && At("."))
            {
                _pos++;
                if (_pos < _tokens.Count
                    && (_tokens[_pos].Kind == AlTokenKind.Identifier
                        || _tokens[_pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    EmitFieldAccessIfResolves(_tokens[_pos], queriedTable);
                    _pos++;
                }
            }

            // Walk the rest of the value (up to the matching `)` of
            // the aggregator) extracting where()/sorting() field refs
            // on the queried table.
            if (queriedTable is not null)
            {
                EmitWhereSortingFieldRefs(queriedTable, stopAt: ";");
            }

            SkipToSemicolon();
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
            _pos++; // property name
            if (!At("=")) { SkipToSemicolon(); return true; }
            _pos++; // =

            var receiver = RecType();
            if (receiver is not null)
            {
                EmitWhereSortingFieldRefs(receiver, stopAt: ";");
            }
            SkipToSemicolon();
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
            while (_pos < _tokens.Count && !At(stopAt))
            {
                var tok = _tokens[_pos];
                if (tok.Kind == AlTokenKind.Identifier
                    && (string.Equals(tok.Value, "where", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tok.Value, "sorting", StringComparison.OrdinalIgnoreCase))
                    && _pos + 1 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == "(")
                {
                    var clauseKind = tok.Value;
                    _pos += 2; // identifier + (
                    EmitFieldsInsideClause(receiver, clauseKind);
                    continue;
                }
                _pos++;
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
            while (_pos < _tokens.Count)
            {
                var tok = _tokens[_pos];

                if (tok.Kind == AlTokenKind.Punct)
                {
                    if (tok.Value == "(")
                    {
                        depth++;
                        _pos++;
                        continue;
                    }
                    if (tok.Value == ")")
                    {
                        if (depth == 0)
                        {
                            _pos++;
                            return;
                        }
                        depth--;
                        _pos++;
                        continue;
                    }
                    if (depth == 0)
                    {
                        if (tok.Value == "=")
                        {
                            expectLhs = false;
                            _pos++;
                            continue;
                        }
                        if (tok.Value == ",")
                        {
                            expectLhs = true;
                            _pos++;
                            continue;
                        }
                    }
                    _pos++;
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
                        EmitFieldAccessIfResolves(tok, receiver);
                    }
                    _pos++;
                    continue;
                }

                _pos++;
            }
        }

        /// <summary>
        /// Emits a <c>field_access</c> reference at <paramref name="tok"/>
        /// when its value resolves to a field on <paramref name="receiver"/>.
        /// Silent no-op otherwise so callers can scan permissively.
        /// </summary>
        private void EmitFieldAccessIfResolves(AlToken tok, AlTypeRef receiver)
        {
            var member = _ctx.Resolver.ResolveMember(receiver, tok.Value);
            if (member is null) return;
            if (!string.Equals(member.Kind, "field", StringComparison.OrdinalIgnoreCase)) return;
            var targetOwner = member.DeclaringType ?? receiver;
            _refs.Add(new ExtractedReference(
                Line: tok.Line,
                Column: tok.Column,
                TargetAppId: targetOwner.AppId,
                TargetObjectKind: targetOwner.Kind,
                TargetObjectId: targetOwner.ObjectId,
                TargetObjectName: targetOwner.Name,
                TargetMemberName: member.Name,
                TargetMemberKind: member.Kind,
                ReferenceKind: "field_access"));
            _resolved++;
        }

        // ── Label declarations + uses (tranche 3) ─────────────────────

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
        private void ScanObjectScopeLabels()
        {
            _pos++; // var keyword

            // Find the bottom (object-scope) frame to mutate.
            ScopeFrame? bottom = null;
            foreach (var f in _scopeStack) bottom = f;
            if (bottom is null) return;

            int braceDepth = 0;
            while (_pos < _tokens.Count)
            {
                var tok = _tokens[_pos];

                if (tok.Kind == AlTokenKind.Punct)
                {
                    if (tok.Value == "{") { braceDepth++; _pos++; continue; }
                    if (tok.Value == "}")
                    {
                        if (braceDepth == 0) return;
                        braceDepth--;
                        _pos++;
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
                    && _pos + 2 < _tokens.Count
                    && _tokens[_pos + 1].Kind == AlTokenKind.Punct
                    && _tokens[_pos + 1].Value == ":"
                    && _tokens[_pos + 2].Kind == AlTokenKind.Identifier
                    && string.Equals(_tokens[_pos + 2].Value, "Label", StringComparison.OrdinalIgnoreCase))
                {
                    var nameLower = tok.Value.ToLowerInvariant();
                    if (!bottom.Vars.ContainsKey(nameLower))
                    {
                        bottom.Vars[nameLower] = new ResolvedVariableType(null, "Label");
                    }
                    _pos += 3;
                    continue;
                }
                _pos++;
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
        private bool TryConsumeLabelUse()
        {
            var tok = _tokens[_pos];
            var key = tok.Value.ToLowerInvariant();
            foreach (var frame in _scopeStack)
            {
                if (!frame.Vars.TryGetValue(key, out var declared)) continue;
                if (!string.Equals(declared.TypeName, "Label", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var owner = OwnerType();
                if (owner is null) return false;
                _refs.Add(new ExtractedReference(
                    Line: tok.Line,
                    Column: tok.Column,
                    TargetAppId: owner.AppId,
                    TargetObjectKind: owner.Kind,
                    TargetObjectId: owner.ObjectId,
                    TargetObjectName: owner.Name,
                    TargetMemberName: tok.Value,
                    TargetMemberKind: "label",
                    ReferenceKind: "label_use"));
                _resolved++;
                _pos++;
                return true;
            }
            return false;
        }

        private void SkipToSemicolon()
        {
            while (_pos < _tokens.Count && !At(";")) _pos++;
            if (At(";")) _pos++;
        }

        /// <summary>
        /// Consumes a top-of-file directive (<c>namespace X.Y.Z;</c> or
        /// <c>using A.B.C;</c>) up to and including the terminating
        /// semicolon. Doesn't touch the references list — these are
        /// pure compile-time annotations the source-viewer doesn't need
        /// to navigate from.
        /// </summary>
        private void SkipToSemicolonAtTopLevel()
        {
            // The directive name token itself.
            _pos++;
            while (_pos < _tokens.Count && !At(";")) _pos++;
            if (At(";")) _pos++;
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
        /// <c>_pos</c> pointing JUST PAST that separator so the regular
        /// dispatch picks up the second / source-expression argument.
        /// </summary>
        private void SkipDslKeywordFirstArg(bool alreadyPastOpenParen = false)
        {
            if (!alreadyPastOpenParen)
            {
                if (!At("(")) return;
                _pos++; // (
            }
            int depth = 0;
            while (_pos < _tokens.Count)
            {
                var tok = _tokens[_pos];
                if (tok.Kind == AlTokenKind.Punct)
                {
                    if (tok.Value == "(") { depth++; _pos++; continue; }
                    if (tok.Value == ")")
                    {
                        if (depth == 0) { _pos++; return; }
                        depth--;
                        _pos++;
                        continue;
                    }
                    if (tok.Value == ";" && depth == 0)
                    {
                        _pos++;
                        return;
                    }
                }
                _pos++;
            }
        }

        // ── EventSubscriber attribute extraction ──────────────────────

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
            var attrLine = _tokens[_pos].Line;
            var attrCol = _tokens[_pos].Column;
            _pos++; // [
            _pos++; // EventSubscriber

            if (!At("("))
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

            var target = _ctx.Resolver.ResolveTypeByName(targetName);
            if (target is null)
            {
                // Cross-release / unknown / numeric id. Silent drop —
                // matches the broader policy for unresolved property
                // references; subscriber attributes against unknown
                // targets aren't a developer error worth reporting.
                SkipPastAttributeClose();
                return;
            }

            _refs.Add(new ExtractedReference(
                Line: attrLine,
                Column: attrCol,
                TargetAppId: target.AppId,
                TargetObjectKind: target.Kind,
                TargetObjectId: target.ObjectId,
                TargetObjectName: target.Name,
                TargetMemberName: eventName,
                TargetMemberKind: "event_publisher",
                ReferenceKind: "event_publisher"));
            _resolved++;

            SkipPastAttributeClose();
        }

        /// <summary>
        /// Reads a parenthesised comma-separated argument list. Assumes
        /// <see cref="_pos"/> is at the opening <c>(</c>. Returns one
        /// token list per arg; leaves <see cref="_pos"/> at the token
        /// immediately after the matching <c>)</c>. Respects nested
        /// parens so embedded expressions don't truncate the split.
        /// </summary>
        private List<List<AlToken>> ReadParenArgs()
        {
            var args = new List<List<AlToken>>();
            if (!At("(")) return args;
            _pos++;
            var current = new List<AlToken>();
            int depth = 0;
            while (_pos < _tokens.Count)
            {
                if (At("("))
                {
                    depth++;
                    current.Add(_tokens[_pos]);
                    _pos++;
                    continue;
                }
                if (At(")"))
                {
                    if (depth == 0)
                    {
                        args.Add(current);
                        _pos++;
                        return args;
                    }
                    depth--;
                    current.Add(_tokens[_pos]);
                    _pos++;
                    continue;
                }
                if (At(",") && depth == 0)
                {
                    args.Add(current);
                    current = new List<AlToken>();
                    _pos++;
                    continue;
                }
                current.Add(_tokens[_pos]);
                _pos++;
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
        /// Advances <see cref="_pos"/> past the matching <c>]</c> of an
        /// attribute we couldn't fully parse. Tracks bracket depth so
        /// nested expressions don't terminate early. Used as the bailout
        /// path when an attribute's args don't match the expected shape.
        /// </summary>
        private void SkipPastAttributeClose()
        {
            int depth = 0;
            while (_pos < _tokens.Count)
            {
                if (At("[")) depth++;
                else if (At("]"))
                {
                    if (depth == 0) { _pos++; return; }
                    depth--;
                }
                _pos++;
            }
        }

        private AlTypeRef? _recTypeCache;
        private bool _recTypeResolved;

        /// <summary>
        /// Lazily resolves Rec's <see cref="AlTypeRef"/> — the receiver
        /// type implicit-field-access uses. For tables / tableextensions
        /// this is the owner type; for pages / pageextensions it's the
        /// SourceTable. BuildGlobalScope already encoded the choice in
        /// the bottom scope frame's <c>rec</c> entry; we read it back
        /// and resolve once.
        /// </summary>
        private AlTypeRef? RecType()
        {
            if (_recTypeResolved) return _recTypeCache;
            _recTypeResolved = true;
            // Bottom frame (object-scope) is where Rec lives. Walk to it.
            ScopeFrame? bottom = null;
            foreach (var frame in _scopeStack) bottom = frame;
            if (bottom is null) return null;
            if (!bottom.Vars.TryGetValue("rec", out var declared)) return null;
            if (string.IsNullOrEmpty(declared.TypeName)) return null;
            // Pass the keyword: Rec on a page is `Record` → must be a
            // table (not a tableextension someone named the same way).
            _recTypeCache = _ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
            return _recTypeCache;
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
        private bool TryConsumeImplicitFieldAccess()
        {
            var head = _tokens[_pos];
            var name = head.Value;

            // 1. Rec must exist.
            var rec = RecType();
            if (rec is null) return false;

            // 2. Skip when name is an in-scope variable.
            var key = name.ToLowerInvariant();
            foreach (var frame in _scopeStack)
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
            var member = _ctx.Resolver.ResolveMember(rec, name);
            if (member is null) return false;
            if (!string.Equals(member.Kind, "field", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var targetOwner = member.DeclaringType ?? rec;
            _refs.Add(new ExtractedReference(
                Line: head.Line,
                Column: head.Column,
                TargetAppId: targetOwner.AppId,
                TargetObjectKind: targetOwner.Kind,
                TargetObjectId: targetOwner.ObjectId,
                TargetObjectName: targetOwner.Name,
                TargetMemberName: member.Name,
                TargetMemberKind: member.Kind,
                ReferenceKind: "field_access"));
            _resolved++;
            _pos++;
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
            foreach (var frame in _scopeStack)
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
                    return _ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
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

        // ── Diagnostics ───────────────────────────────────────────────

        /// <summary>
        /// Captures an unresolved reference up to <see cref="UnresolvedSampleCap"/>
        /// per file. The receiver type (when known) is recorded as a
        /// <c>kind:name</c> pair so the import-side aggregator can show
        /// "all chain-steps on table:Customer that failed" without re-running.
        /// Pass a <paramref name="receiverNameOverride"/> when the receiver
        /// isn't a resolved AlTypeRef (e.g. a typed-literal whose name didn't
        /// resolve — the caller has only the unresolved name to record).
        /// </summary>
        private void CaptureUnresolved(
            string reason, AlToken tok, AlTypeRef? receiver, string? receiverNameOverride = null)
        {
            if (_unresolvedSamples.Count >= UnresolvedSampleCap) return;
            _unresolvedSamples.Add(new UnresolvedSample(
                Reason: reason,
                Token: tok.Value,
                Line: tok.Line,
                Column: tok.Column,
                ReceiverKind: receiver?.Kind,
                ReceiverName: receiver?.Name ?? receiverNameOverride));
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
        private static bool IsPlatformVirtualTableId(string typeName) =>
            int.TryParse(typeName, out var id)
            && id >= 2000000000
            && id <= 2000000999;

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
    string ReferenceKind);

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
    string? ReceiverName = null);

/// <summary>Result envelope: extracted rows plus the run's stats.</summary>
public sealed record AlExtractionResult(
    IReadOnlyList<ExtractedReference> References,
    ExtractionStats Stats);
