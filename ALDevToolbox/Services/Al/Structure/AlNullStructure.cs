namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Fallback for owner kinds we don't model with a dedicated structure
/// extractor yet — codeunit, interface, permissionset, report, query,
/// xmlport, enum, controladdin, profile, pagecustomization,
/// entitlement, requestpage. Returns <c>false</c> for every token so
/// the orchestrator's shared dispatch handles object-scope work.
///
/// When one of these owner kinds accumulates kind-specific DSL that
/// the generic dispatch can't handle correctly (e.g. report's
/// <c>dataitem</c> / <c>column</c> emitting <c>report_column</c>
/// symbols, query's <c>filter</c> / <c>orderby</c> clauses), replace
/// the dispatch entry in <see cref="AlReferenceExtractor"/> with a
/// purpose-built structure file.
/// </summary>
internal sealed class AlNullStructure : IAlObjectStructureExtractor
{
    public bool TryConsumeObjectScopeToken(AlToken tok) => false;
}
