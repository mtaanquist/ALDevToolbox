using System.Collections.Generic;

namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>table</c> and <c>tableextension</c>
/// owner kinds. Owns table-side <c>field(&lt;id&gt;; "&lt;name&gt;";
/// &lt;type&gt;)</c> declarations (extracting the AL-object-typed
/// type reference when present) and lets the orchestrator's shared
/// property handlers cover the rest (<c>TableRelation</c>,
/// <c>CalcFormula</c>, <c>Permissions</c>, etc.).
///
/// Removes the table-vs-page peek-and-branch the unified walker
/// pre-refactor needed: this extractor only ever runs on a table
/// owner, so it can assume the table-side <c>(id; name; type)</c>
/// shape and skip the form detection. See
/// <c>.design/al-reference-extractor-refactor.md</c> step 4.
/// </summary>
internal sealed class AlTableStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlTableStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        // procedureWalker is held for future per-kind logic that needs
        // body access (e.g. step 6 variable_use emission threading).
        // Unused today but accepted at construction so the orchestrator
        // can wire dependencies uniformly across per-kind extractors.
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return false;

        if (string.Equals(tok.Value, "field", StringComparison.OrdinalIgnoreCase)
            && _state.Pos + 1 < _state.Tokens.Count
            && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
            && _state.Tokens[_state.Pos + 1].Value == "(")
        {
            TryConsumeFieldDeclaration();
            return true;
        }

        if (string.Equals(tok.Value, "modify", StringComparison.OrdinalIgnoreCase)
            && _state.Pos + 1 < _state.Tokens.Count
            && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
            && _state.Tokens[_state.Pos + 1].Value == "(")
        {
            TryConsumeFieldModification();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a table-side <c>field(&lt;id&gt;; "&lt;name&gt;";
    /// &lt;type&gt;)</c> declaration and emits a reference for the
    /// type when it's an AL object (typically
    /// <c>Enum "Sales Document Type"</c>). Scalar types
    /// (<c>Code[20]</c>, <c>Integer</c>, etc.) fall through silently.
    ///
    /// Defensive guard: if the first inside-paren token isn't a
    /// number the line isn't actually a table-side declaration — a
    /// page-form <c>field("Ctl"; Rec.X)</c> might be sitting inside
    /// a misconfigured fixture. Skip the first arg generically so
    /// the orchestrator's main loop walks the remainder; emitting a
    /// table-field row on a clearly page-shaped line would be worse
    /// than the silent skip.
    /// </summary>
    private void TryConsumeFieldDeclaration()
    {
        _state.Pos++; // field
        if (!_state.At("(")) return;
        _state.Pos++; // (

        if (_state.Pos >= _state.Tokens.Count) return;
        if (_state.Tokens[_state.Pos].Kind != AlTokenKind.Number)
        {
            _state.SkipDslKeywordFirstArg(alreadyPastOpenParen: true);
            return;
        }

        // (id; name; type). We only care about arg[2] — the type.
        int depth = 0;
        int semicolonsSeen = 0;
        var typeTokens = new List<AlToken>();
        while (_state.Pos < _state.Tokens.Count)
        {
            var tok = _state.Tokens[_state.Pos];
            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "(") { depth++; _state.Pos++; continue; }
                if (tok.Value == ")")
                {
                    if (depth == 0) { _state.Pos++; break; }
                    depth--;
                    _state.Pos++;
                    continue;
                }
                if (tok.Value == ";" && depth == 0)
                {
                    semicolonsSeen++;
                    _state.Pos++;
                    continue;
                }
            }
            if (semicolonsSeen == 2)
            {
                typeTokens.Add(tok);
            }
            _state.Pos++;
        }

        EmitTypedReference(typeTokens);
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

        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var kw = tokens[i];
            if (kw.Kind != AlTokenKind.Identifier) continue;
            if (!AlExtractionState.IsAlObjectKeyword(kw.Value)) continue;
            var nameTok = tokens[i + 1];
            if (nameTok.Kind != AlTokenKind.Identifier
                && nameTok.Kind != AlTokenKind.QuotedIdentifier)
            {
                continue;
            }
            var target = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value);
            if (target is null) return;
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
            return;
        }
    }

    /// <summary>
    /// Reads a tableextension-side <c>modify("&lt;field name&gt;")</c>
    /// construct and emits a <c>field_access</c> reference for the
    /// named field. The name targets a field on the base table the
    /// extension is attached to (<see cref="AlExtractContext.OwnerSourceTableName"/>
    /// holds it after the importer copies <c>ExtendsObjectName</c>
    /// through).
    /// <para>
    /// Without this handler the modified field token slipped past the
    /// object-scope dispatch entirely and got no underline / no
    /// resolvable, even though Go-to-definition on the same token
    /// would have worked once the underline pointed users at it.
    /// </para>
    /// <para>
    /// Defensive guard: if the base table can't be resolved (the
    /// extension targets a table outside the imported release, for
    /// example), skip silently rather than emit an unresolved row —
    /// the noise would dominate any real signal.
    /// </para>
    /// </summary>
    private void TryConsumeFieldModification()
    {
        _state.Pos++; // modify
        if (!_state.At("(")) return;
        _state.Pos++; // (
        if (_state.Pos >= _state.Tokens.Count) return;

        var nameTok = _state.Tokens[_state.Pos];
        if (nameTok.Kind == AlTokenKind.Identifier
            || nameTok.Kind == AlTokenKind.QuotedIdentifier)
        {
            EmitBaseTableFieldReference(nameTok);
        }

        // Walk to the closing `)` so the orchestrator picks up
        // wherever modify(...)'s body block follows (the per-trigger
        // dispatch lives in the procedure walker, not here).
        int depth = 0;
        while (_state.Pos < _state.Tokens.Count)
        {
            var tok = _state.Tokens[_state.Pos];
            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "(") { depth++; }
                else if (tok.Value == ")")
                {
                    if (depth == 0) { _state.Pos++; return; }
                    depth--;
                }
            }
            _state.Pos++;
        }
    }

    private void EmitBaseTableFieldReference(AlToken nameTok)
    {
        var baseTableName = _state.Ctx.OwnerSourceTableName;
        if (string.IsNullOrEmpty(baseTableName)) return;
        var baseTable = _state.Ctx.Resolver.ResolveTypeByName(baseTableName, "table");
        if (baseTable is null) return;
        var member = _state.Ctx.Resolver.ResolveMember(baseTable, nameTok.Value);
        if (member is null || !AlExtractionState.IsFieldKind(member.Kind)) return;
        var targetOwner = member.DeclaringType ?? baseTable;
        _state.EmitReference(new ExtractedReference(
            Line: nameTok.Line,
            Column: nameTok.Column,
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
