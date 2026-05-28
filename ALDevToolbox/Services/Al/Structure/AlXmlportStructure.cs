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
    /// </summary>
    public void Prescan() => AlDataItemDsl.PrescanAliases(_state, "tableelement");
}
