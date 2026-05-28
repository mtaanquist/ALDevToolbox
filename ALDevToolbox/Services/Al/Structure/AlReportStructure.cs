namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>report</c> and
/// <c>reportextension</c> owner kinds. Owns
/// <c>dataitem(Alias; SourceTable)</c> declarations — including
/// nested dataitems where a child references the parent's alias
/// (<c>DataItemLink = "Field" = ParentAlias."Field";</c>).
///
/// Tracks the most-recent dataitem's source table so bare field
/// references inside <c>column(name; SourceField)</c> resolve
/// against the right table. See <c>AlQueryStructure</c> for the
/// most-recent-wins rationale.
///
/// Reports also embed a <c>requestpage { … }</c> block whose layout
/// uses page-style <c>field(Name; expr)</c> control declarations.
/// Those keep the generic DSL first-arg skip — the orchestrator's
/// fallthrough handles them.
/// </summary>
internal sealed class AlReportStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlReportStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok)
    {
        var (consumed, source) = AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "dataitem", tok);
        if (consumed && source is not null)
        {
            // Promoted to shared state so the bare-self-call resolver
            // sees it too — `AutoFormatExpression = GetProc();` inside
            // a column needs to resolve GetProc against the dataitem's
            // source table.
            _state.CurrentDataItemSource = source;
        }
        return consumed;
    }

    public bool TryResolveObjectScopeBareIdentifier(AlToken tok) =>
        AlDataItemDsl.TryEmitBareFieldOnSource(_state, _state.CurrentDataItemSource, tok);

    /// <summary>
    /// Reports declare <c>dataitem(Alias; SourceTable)</c> inside the
    /// <c>dataset { }</c> block. Aliases get referenced from earlier-
    /// declared properties / triggers — the canonical shape is the
    /// <c>WordMergeDataItem = TempSegmentLine;</c> property pointing
    /// to a dataitem declared later in the same dataset
    /// (ContactCoverSheet.Report:23 references the dataitem declared
    /// at :79). Same forward-reference problem the xmlport handler
    /// already solves; route through the shared pre-scan.
    /// </summary>
    public void Prescan() => AlDataItemDsl.PrescanAliases(_state, "dataitem");
}
