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
        }
    }
}
