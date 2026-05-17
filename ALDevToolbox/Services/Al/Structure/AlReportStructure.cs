namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>report</c> and
/// <c>reportextension</c> owner kinds. Owns
/// <c>dataitem(Alias; SourceTable)</c> declarations — including
/// nested dataitems where a child references the parent's alias
/// (<c>DataItemLink = "Field" = ParentAlias."Field";</c>).
///
/// Reports also embed a <c>requestpage { … }</c> block whose layout
/// uses page-style <c>field(Name; expr)</c> control declarations.
/// Those keep the generic DSL first-arg skip — the orchestrator's
/// fallthrough handles them. A future per-kind <c>requestpage</c>
/// extractor could resolve their source expressions properly.
/// </summary>
internal sealed class AlReportStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlReportStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok) =>
        AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "dataitem", tok);
}
