namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>query</c> owner kind. Owns
/// <c>dataitem(Alias; SourceTable)</c> declarations: the alias is
/// registered in the outermost scope frame so subsequent
/// <c>Alias."FieldName"</c> chains (notably in
/// <c>DataItemLink = "Vendor No." = QueryElement1."No.";</c>)
/// resolve through the chain walker instead of failing with
/// <c>head-not-a-variable</c>.
///
/// <c>column(Name; SourceExpression)</c> and
/// <c>filter(Name; SourceExpression)</c> keep the generic DSL
/// first-arg skip — their source expressions are field references
/// on the enclosing dataitem's source table that the implicit-Rec
/// path doesn't model yet (left for a later per-dataitem-context
/// pass).
/// </summary>
internal sealed class AlQueryStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlQueryStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok) =>
        AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "dataitem", tok);
}
