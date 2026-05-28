namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>query</c> owner kind. Owns
/// <c>dataitem(Alias; SourceTable)</c> declarations: registers the
/// alias on the outermost scope frame so subsequent
/// <c>Alias."FieldName"</c> chains (notably in
/// <c>DataItemLink = "Vendor No." = QueryElement1."No.";</c>)
/// resolve through the chain walker.
///
/// Tracks the most-recent dataitem's source table so bare field
/// references inside <c>column(name; "No.")</c> /
/// <c>filter(name; "Posting Date")</c> source expressions resolve
/// against the right receiver. Nested dataitems are handled
/// most-recent-wins — AL grammar in practice places columns /
/// filters inside their dataitem's body before the next dataitem
/// begins, so the slot doesn't need a stack. A late column declared
/// AFTER a nested dataitem closes would mis-resolve; rare enough to
/// accept for now.
/// </summary>
internal sealed class AlQueryStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlQueryStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok)
    {
        var (consumed, source) = AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "dataitem", tok);
        if (consumed && source is not null)
        {
            _state.CurrentDataItemSource = source;
        }
        return consumed;
    }

    public bool TryResolveObjectScopeBareIdentifier(AlToken tok) =>
        AlDataItemDsl.TryEmitBareFieldOnSource(_state, _state.CurrentDataItemSource, tok);
}
