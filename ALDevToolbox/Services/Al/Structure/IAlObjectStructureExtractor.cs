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

    /// <summary>
    /// Called for a bare identifier or quoted identifier at object
    /// scope that no other dispatch consumed — no following <c>.</c>,
    /// <c>::</c>, <c>(</c>, or <c>=</c>. Lets per-kind extractors that
    /// track context (query / report / xmlport tracking the current
    /// <c>dataitem</c> / <c>tableelement</c> source table) emit
    /// field_access on bare field references inside
    /// <c>column(name; expr)</c>, <c>filter(name; expr)</c>,
    /// <c>fieldelement(name; expr)</c> style source expressions.
    ///
    /// Returns <c>true</c> when consumed (cursor advanced past the
    /// token); <c>false</c> to let the orchestrator fall through to
    /// its default single-token advance. Default no-op so kinds
    /// without dataitem context stay minimal.
    /// </summary>
    bool TryResolveObjectScopeBareIdentifier(AlToken tok) => false;

    /// <summary>
    /// Runs once before the main token walk, with the cursor at the
    /// start of the file. Lets per-kind extractors register lexical
    /// constructs that the main walk will need to look up from
    /// procedure bodies declared earlier in the same file.
    ///
    /// The dominant case is xmlport: tableelements live in the
    /// <c>schema { }</c> block, which BC's Base App routinely places
    /// AFTER the procedure block. Without a pre-scan, every
    /// <c>tableelement-alias.Member</c> chain in a procedure body
    /// fires head-not-a-variable because the alias hasn't been
    /// registered yet. The pre-scan walks tableelement declarations
    /// up front and seeds the outer scope frame.
    ///
    /// Default no-op so kinds without forward-reference shapes
    /// stay minimal. Implementations MUST restore
    /// <see cref="AlExtractionState.Pos"/> to its entry value before
    /// returning so the main walk starts from the top.
    /// </summary>
    void Prescan() { }
}
