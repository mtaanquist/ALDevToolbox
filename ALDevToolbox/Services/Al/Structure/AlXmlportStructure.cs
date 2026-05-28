namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>xmlport</c> owner kind. Owns
/// <c>tableelement(Alias; SourceTable)</c> declarations — same
/// alias-plus-source shape as report / query <c>dataitem</c>.
///
/// Tracks the most-recent tableelement's source table so bare
/// field references inside
/// <c>fieldattribute(Name; SourceField)</c> /
/// <c>fieldelement(Name; SourceField)</c> source expressions
/// resolve. Chain forms (<c>fieldelement(Description; ItemRow.Description)</c>)
/// are already handled by the chain walker via the registered
/// alias.
///
/// <c>textelement(Name)</c> keeps the generic DSL first-arg skip.
/// </summary>
internal sealed class AlXmlportStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlXmlportStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok)
    {
        var (consumed, source) = AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "tableelement", tok);
        if (consumed && source is not null)
        {
            _state.CurrentDataItemSource = source;
        }
        return consumed;
    }

    public bool TryResolveObjectScopeBareIdentifier(AlToken tok) =>
        AlDataItemDsl.TryEmitBareFieldOnSource(_state, _state.CurrentDataItemSource, tok);

    /// <summary>
    /// XmlPort schema blocks declare <c>tableelement(alias; SourceTable)</c>
    /// constructs whose aliases are referenced inside procedure bodies
    /// in the same file. Base App routinely places the schema block
    /// AFTER the procedure block (e.g. SEPADDpain00800102.XmlPort.al
    /// uses <c>paymentexportdatagroup.GetOrganizationID()</c> at
    /// line 103 with the declaration at line 111). Walk every
    /// tableelement up front and seed the outermost scope frame so
    /// the main walk's chain resolver finds the alias.
    ///
    /// The pre-scan only touches tableelement declarations — it
    /// doesn't emit references for source-table names (those still
    /// emit on the main pass via TryConsumeAliasedSourceDeclaration).
    /// Restores the cursor before returning so the main walk runs
    /// from the file's first token.
    /// </summary>
    public void Prescan()
    {
        int savedPos = _state.Pos;
        try
        {
            _state.Pos = 0;
            while (_state.Pos < _state.Tokens.Count)
            {
                var tok = _state.Tokens[_state.Pos];
                if (tok.Kind == AlTokenKind.Identifier
                    && string.Equals(tok.Value, "tableelement", StringComparison.OrdinalIgnoreCase)
                    && _state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 1].Value == "(")
                {
                    RegisterTableElementAlias();
                    continue;
                }
                _state.Pos++;
            }
        }
        finally
        {
            _state.Pos = savedPos;
        }
    }

    /// <summary>
    /// Reads <c>tableelement(alias; SourceTable)</c> at the current
    /// position and stamps the alias onto the outermost scope frame
    /// when the source table resolves through the catalog. Mirrors
    /// the alias-registration half of
    /// <see cref="AlDataItemDsl.TryConsumeAliasedSourceDeclaration"/>
    /// without emitting the property_object reference (that's still
    /// the main pass's job — pre-scan only seeds scope, doesn't
    /// double-emit references).
    /// </summary>
    private void RegisterTableElementAlias()
    {
        // We arrive at `tableelement`; advance past it and the `(`.
        _state.Pos += 2;

        string? alias = null;
        if (_state.Pos < _state.Tokens.Count
            && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
        {
            alias = _state.Tokens[_state.Pos].Value;
            _state.Pos++;
        }

        // Skip to the `;` separator at depth 0.
        int depth = 0;
        while (_state.Pos < _state.Tokens.Count)
        {
            var current = _state.Tokens[_state.Pos];
            if (current.Kind == AlTokenKind.Punct)
            {
                if (current.Value == "(") { depth++; _state.Pos++; continue; }
                if (current.Value == ")")
                {
                    if (depth == 0) return;
                    depth--;
                    _state.Pos++;
                    continue;
                }
                if (current.Value == ";" && depth == 0)
                {
                    _state.Pos++;
                    break;
                }
            }
            _state.Pos++;
        }

        // Second arg: source table. May be preceded by `Record`.
        if (_state.Pos < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
            && AlExtractionState.IsAlObjectKeyword(_state.Tokens[_state.Pos].Value))
        {
            _state.Pos++;
        }
        if (_state.Pos >= _state.Tokens.Count) return;

        var sourceTok = _state.Tokens[_state.Pos];
        if (sourceTok.Kind != AlTokenKind.Identifier
            && sourceTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            return;
        }

        if (!string.IsNullOrEmpty(alias))
        {
            var resolved = _state.Ctx.Resolver.ResolveTypeByName(sourceTok.Value, "Record");
            if (resolved is not null
                && string.Equals(resolved.Kind, "table", StringComparison.OrdinalIgnoreCase))
            {
                ScopeFrame? bottom = null;
                foreach (var frame in _state.ScopeStack) bottom = frame;
                if (bottom is not null)
                {
                    bottom.Vars[alias.ToLowerInvariant()] =
                        new ResolvedVariableType("Record", resolved.Name);
                }
            }
        }
        _state.Pos++;
    }
}
