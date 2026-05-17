namespace ALDevToolbox.Services.Al.Structure;

/// <summary>
/// Per-owner-kind handler for AL object-scope DSL constructs. The
/// orchestrator nested in <see cref="AlReferenceExtractor"/> dispatches
/// each object-scope token through one of these before falling back to
/// shared property handlers — so <c>field(N; "Name"; Type)</c> on a
/// table and <c>field("Ctl"; Rec.X)</c> on a page can route to
/// different code paths instead of sharing a peek-and-branch.
///
/// See <c>.design/al-reference-extractor-refactor.md</c> step 4.
/// Implementations share mutable state through
/// <see cref="AlExtractionState"/> and delegate body work to
/// <see cref="AlProcedureWalker"/>.
///
/// Composition over inheritance — concrete implementations take state
/// + walker as constructor dependencies. A base class with virtuals
/// would hide kind-specific branching behind dispatch tables.
/// </summary>
internal interface IAlObjectStructureExtractor
{
    /// <summary>
    /// Tries to consume the current token as a kind-specific DSL
    /// construct. Returns <c>true</c> when the structure extractor
    /// handled it (cursor advanced past the construct); <c>false</c>
    /// to let the orchestrator fall through to the shared dispatch
    /// (property handlers, generic DSL keyword skip, etc.).
    /// Called only at object scope (when the procedure walker's
    /// scope stack has just the outermost frame).
    /// </summary>
    bool TryConsumeObjectScopeToken(AlToken tok);

    /// <summary>
    /// Tries to consume the current object-scope property
    /// (<c>&lt;name&gt; = &lt;value&gt;;</c>) when the property name
    /// is one the per-kind extractor wants to own. Called by the
    /// orchestrator before its shared property dispatch, so a
    /// per-kind handler beats the generic one. Returns <c>true</c>
    /// when consumed (cursor advanced past the trailing
    /// <c>;</c>); <c>false</c> to fall through to the shared
    /// dispatch.
    ///
    /// Default no-op so kinds without per-property logic stay
    /// minimal. <c>AlPageStructure</c> overrides this for
    /// <c>SubPageLink</c> / <c>RunPageLink</c> — properties whose
    /// LHS field names belong to the TARGET page's source table,
    /// not the current page's Rec (step 5).
    /// </summary>
    bool TryConsumeObjectScopeProperty(string propertyName) => false;
}
