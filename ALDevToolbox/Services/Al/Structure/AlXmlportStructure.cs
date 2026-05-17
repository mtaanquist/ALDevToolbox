namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Object-scope DSL handler for <c>xmlport</c> owner kind. Owns
/// <c>tableelement(Alias; SourceTable)</c> declarations — same
/// alias-plus-source shape as report / query <c>dataitem</c>.
/// Registering the alias lets nested <c>fieldattribute</c> /
/// <c>fieldelement</c> source expressions (e.g.
/// <c>fieldelement(Description; ItemRow.Description)</c>) resolve
/// the <c>ItemRow</c> qualifier through the chain walker.
///
/// <c>textelement(Name)</c>, <c>fieldattribute(Name; Expr)</c>, and
/// <c>fieldelement(Name; Expr)</c> keep the generic DSL first-arg
/// skip.
/// </summary>
internal sealed class AlXmlportStructure : IAlObjectStructureExtractor
{
    private readonly AlExtractionState _state;

    public AlXmlportStructure(AlExtractionState state, AlProcedureWalker procedureWalker)
    {
        _state = state;
        _ = procedureWalker;
    }

    public bool TryConsumeObjectScopeToken(AlToken tok) =>
        AlDataItemDsl.TryConsumeAliasedSourceDeclaration(_state, "tableelement", tok);
}
