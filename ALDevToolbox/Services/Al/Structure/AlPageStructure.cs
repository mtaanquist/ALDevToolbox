namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>page</c>, <c>pageextension</c>, and
/// <c>requestpage</c> owner kinds. Owns:
/// <list type="bullet">
///   <item>Page-side <c>field("Ctl"; sourceExpr)</c> control
///         declarations — skip the control name, let the orchestrator
///         walk the source expression so chains like
///         <c>Rec."Field Name"</c> emit their references through the
///         shared dispatch.</item>
///   <item><c>part(Ctl; "Page Name")</c> and
///         <c>systempart(Ctl; SystemPartType)</c> — skip control name,
///         resolve the page reference in the second arg.</item>
///   <item>Layout / action grouping keywords with a first-arg control
///         name (<c>area</c>, <c>group</c>, <c>repeater</c>,
///         <c>cuegroup</c>, <c>action</c>, <c>actionref</c>,
///         <c>modify</c>, <c>addAfter</c>, <c>addLast</c>, etc.) —
///         skip the first arg so it doesn't get mis-emitted as an
///         implicit-Rec field access.</item>
/// </list>
///
/// Step 5 of the refactor (cross-page <c>SubPageLink</c> /
/// <c>RunPageLink</c> field resolution) lands here once the part
/// tracking is in place — the structure extractor can record the
/// most-recent part's PageRef and resolve <c>field("X")</c> inside
/// <c>SubPageLink</c> against that target page's source table.
/// See <c>.design/al-reference-extractor-refactor.md</c> step 4.
/// </summary>
internal sealed class AlPageStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    /// <summary>
    /// Most-recent part declaration's resolved target source table.
    /// Updated when <see cref="TryConsumePartDeclaration"/> resolves
    /// a <c>part(Ctl; "TargetPage")</c> whose target page has a
    /// SourceTable in the catalog. SubPageLink / RunPageLink LHS
    /// field names key off this — they belong to the TARGET page's
    /// source table, not the current page's Rec. AL grammar puts
    /// each part's per-part properties (SubPageLink included)
    /// immediately inside the part's <c>{ }</c> block before the
    /// next part begins, so a single most-recent-wins slot is
    /// enough; we don't need a stack.
    /// </summary>
    private AlTypeRef? _currentPartTargetSourceTable;

    public AlPageStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker; // see AlTableStructure
    }

    public bool TryConsumeObjectScopeToken(AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return false;
        if (_state.Pos + 1 >= _state.Tokens.Count) return false;
        var next = _state.Tokens[_state.Pos + 1];
        if (next.Kind != AlTokenKind.Punct || next.Value != "(") return false;

        // field("Ctl"; sourceExpr) — page-side. The first arg is the
        // page-field name (a local control declaration, NOT a
        // navigation target — see step 1's IsNonNavigableDeclaration
        // filter), the second is a source expression the main loop
        // walks through the shared dispatch (so a `Rec."Field"`
        // expression emits its field_access reference as usual).
        if (string.Equals(tok.Value, "field", StringComparison.OrdinalIgnoreCase))
        {
            _state.Pos++; // field
            _state.SkipDslKeywordFirstArg();
            return true;
        }

        // part / systempart: control name + page-or-enum value.
        if (string.Equals(tok.Value, "part", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tok.Value, "systempart", StringComparison.OrdinalIgnoreCase))
        {
            TryConsumePartDeclaration();
            return true;
        }

        // Layout / action grouping with a control-name first arg.
        // AlBuiltinMethods.IsObjectDslKeyword spans every kind's DSL
        // keywords — same set for page/table/report — but for the
        // page structure extractor only the ones that legitimately
        // appear in a page body fire here. The generic test catches
        // the rest.
        if (AlBuiltinMethods.IsObjectDslKeyword(tok.Value))
        {
            _state.Pos++; // the DSL keyword itself
            _state.SkipDslKeywordFirstArg();
            return true;
        }

        return false;
    }

    public bool TryConsumeObjectScopeProperty(string propertyName)
    {
        if (string.Equals(propertyName, "SubPageLink", StringComparison.OrdinalIgnoreCase)
            || string.Equals(propertyName, "RunPageLink", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeCrossPageFieldLinkProperty();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Handles <c>SubPageLink = "TargetField" = field("CurrentField") [,
    /// "TargetField2" = field("CurrentField2")]*;</c> and the identical
    /// shape <c>RunPageLink</c>. Each LHS field name belongs to the
    /// most-recent part's target page source-table
    /// (<see cref="_currentPartTargetSourceTable"/>); RHS
    /// <c>field(...)</c> references the current page's Rec, which the
    /// orchestrator's main dispatch silences via the generic DSL-
    /// keyword first-arg skip (so no spurious self-emission). Emits
    /// one <c>field_access</c> per LHS that resolves on the target
    /// table. Bails to a generic skip-to-semicolon when the part
    /// target isn't known. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 5.
    /// </summary>
    private void ConsumeCrossPageFieldLinkProperty()
    {
        _state.Pos++; // property name
        if (!_state.At("=")) { _state.SkipToSemicolon(); return; }
        _state.Pos++; // =

        var target = _currentPartTargetSourceTable;
        if (target is null) { _state.SkipToSemicolon(); return; }

        // Walk value tokens until `;`. Each `<LHS> = ...` pair: the
        // LHS is on the target table. Reset to "expect-LHS" on commas
        // (next pair); flip to "expect-RHS" past `=` so we don't
        // re-emit the RHS expression's identifiers.
        bool expectLhs = true;
        int depth = 0;
        while (_state.Pos < _state.Tokens.Count && !_state.At(";"))
        {
            var tok = _state.Tokens[_state.Pos];

            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "(") { depth++; _state.Pos++; continue; }
                if (tok.Value == ")")
                {
                    if (depth > 0) depth--;
                    _state.Pos++;
                    continue;
                }
                if (depth == 0)
                {
                    if (tok.Value == "=") { expectLhs = false; _state.Pos++; continue; }
                    if (tok.Value == ",") { expectLhs = true; _state.Pos++; continue; }
                }
                _state.Pos++;
                continue;
            }

            if (depth == 0
                && expectLhs
                && (tok.Kind == AlTokenKind.Identifier || tok.Kind == AlTokenKind.QuotedIdentifier))
            {
                var member = _state.Ctx.Resolver.ResolveMember(target, tok.Value);
                if (member is not null && AlExtractionState.IsFieldKind(member.Kind))
                {
                    var targetOwner = member.DeclaringType ?? target;
                    _state.Refs.Add(new ExtractedReference(
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
            _state.Pos++;
        }
        if (_state.At(";")) _state.Pos++;
    }

    /// <summary>
    /// Handles <c>part(ControlName; "Page Name")</c> and
    /// <c>systempart(ControlName; SystemPartType)</c> on pages. The
    /// first arg is the part's control name (a declaration — skip),
    /// the second is the page reference we resolve to its catalog
    /// entry. Emits a <c>property_object</c> reference on the page-
    /// name token so Find references / Go to definition / the
    /// underline all work the same way they do on
    /// <c>RunObject = Page "X"</c>.
    /// </summary>
    private void TryConsumePartDeclaration()
    {
        _state.Pos++; // part / systempart
        if (!_state.At("(")) return;
        _state.Pos++; // (

        // First arg: control name. Skip through to the `;` separator.
        _state.SkipDslKeywordFirstArg(alreadyPastOpenParen: true);
        if (_state.Pos >= _state.Tokens.Count) return;

        // Second arg: the page reference. Optionally preceded by the
        // `Page` keyword (rare in part declarations but legal).
        if (_state.Pos < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
            && AlExtractionState.IsAlObjectKeyword(_state.Tokens[_state.Pos].Value))
        {
            _state.Pos++;
        }
        if (_state.Pos >= _state.Tokens.Count) return;
        var nameTok = _state.Tokens[_state.Pos];
        if (nameTok.Kind != AlTokenKind.Identifier
            && nameTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return;
        }

        var target = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value, "Page");
        if (target is not null)
        {
            _state.Refs.Add(new ExtractedReference(
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

            // Step 5: record the target page's source table so a
            // subsequent SubPageLink / RunPageLink can resolve LHS
            // field names against it. Misses when the target page
            // isn't catalogued with a SourceTable (the test stub
            // resolver's default behaviour, or a kind that doesn't
            // carry one) — we just leave the previous part's
            // target intact, which AL grammar would have overwritten
            // before any SubPageLink runs anyway.
            var sourceTableName = _state.Ctx.Resolver.ResolveSourceTableName(target);
            if (!string.IsNullOrEmpty(sourceTableName))
            {
                var sourceTable = _state.Ctx.Resolver.ResolveTypeByName(sourceTableName, "Record");
                if (sourceTable is not null)
                {
                    _currentPartTargetSourceTable = sourceTable;
                }
            }
        }
    }
}
