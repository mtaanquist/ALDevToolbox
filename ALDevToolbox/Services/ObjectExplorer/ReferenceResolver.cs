using ALDevToolbox.Services.Al;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Resolves a clicked token at a known (file, line, column) to its
/// Object-Explorer-side target — either a catalog <c>OeModuleObject</c>
/// or an <c>OeModuleSymbol</c> (procedure / field / trigger). Lifted
/// from <see cref="ReferenceSessionService.CreateAtPositionAsync"/> so
/// the extractor side and the click-time side can grow a single shared
/// strategy chain instead of drifting in parallel. See
/// <c>.design/al-reference-extractor-refactor.md</c> step 3.
///
/// <para><b>Strategies tried, in order:</b></para>
/// <list type="number">
///   <item><c>extracted-ref</c> — an extracted reference recorded on
///         this file at the clicked line whose target name matches
///         the clicked word. Disambiguates kind collisions
///         ("General Ledger Setup" exists as both a Table and a
///         Page) using the import-time AL-keyword context.</item>
///   <item><c>object-name</c> — the word matches an object name in
///         the release (case-insensitive).</item>
///   <item><c>variable-member</c> — qualified <c>var.Member</c> click:
///         the qualifier's declared type plus the clicked member name
///         resolves to a symbol.</item>
///   <item><c>variable-type-fallback</c> — same as <c>variable-member</c>
///         but the symbol lookup misses, so we fall back to "references
///         to the receiver type" so the user gets something useful.</item>
///   <item><c>local-member</c> — bare member click on a declaration
///         in the current file (no qualifier).</item>
/// </list>
///
/// Step 3b will add a <c>global-variable</c> strategy on top of these,
/// consulting the position-augmented <c>oe_module_variables</c> rows
/// from step 2. Step 4 plumbs the extractor's Phase-2 emission through
/// the same resolver.
/// </summary>
public sealed class ReferenceResolver
{
    private readonly Data.AppDbContext _db;

    public ReferenceResolver(Data.AppDbContext db)
    {
        _db = db;
    }

    public async Task<ReferenceResolution?> ResolveAsync(
        ResolverContext ctx, CancellationToken ct = default)
    {
        // Strategy: local-variable. Runs FIRST so a click on a
        // procedure-local or object-scope `var` name short-circuits
        // before the type-name strategies below get a chance to
        // accidentally bind to the variable's underlying type. The
        // user-reported repro: right-clicking `BankAccount` (a local
        // `Record "Bank Account"`) used to surface every reference
        // to the Bank Account table across the whole release —
        // because some downstream strategy fell back to the type.
        // Returning the LocalVariable target here makes Find
        // references show same-file occurrences and Go-to-definition
        // land on the declaration line, which is what the user
        // expects from a local-variable click. The corresponding
        // click on the underlined type-name token (`"Bank Account"`,
        // whose word is the type name not the variable name) still
        // resolves through extracted-ref / object-name below.
        // Only bare-context clicks engage the local-variable check. Skip it
        // when the user clicked a member chain's RIGHT side (`var.Member` /
        // `Var::Y`) — those always go through the member resolver paths below.
        if (string.IsNullOrEmpty(ctx.LeftQualifier)
            && AlGoToDefinitionLocator.ResolveVariableDeclarationLine(ctx.FileContent, ctx.Word) is not null)
        {
            return new ReferenceResolution(
                new ResolutionTarget.LocalVariable(ctx.FileId, ctx.Word),
                "local-variable");
        }

        // Strategy: extracted-ref — token-level disambiguation via
        // the import-time reference rows.
        var precise = await ResolveExtractedReferenceAtAsync(
            ctx.FileId, ctx.ReleaseId, ctx.Line, ctx.Word, ct);
        if (precise is not null)
        {
            return new ReferenceResolution(new ResolutionTarget.CatalogObject(precise.Id), "extracted-ref");
        }

        // Strategy: object-name — direct case-insensitive object lookup.
        var byObject = await ResolveObjectByNameAsync(ctx.ReleaseId, ctx.Word, ct);
        if (byObject is not null)
        {
            return new ReferenceResolution(new ResolutionTarget.CatalogObject(byObject.Id), "object-name");
        }

        // Strategy: qualified `var.Member` — variable-member, with a
        // variable-type-fallback when the member lookup misses.
        if (ctx.LeftOperator == "." && !string.IsNullOrEmpty(ctx.LeftQualifier))
        {
            var receiverType = AlGoToDefinitionLocator.ResolveVariableType(
                ctx.FileContent, ctx.LeftQualifier);
            if (!string.IsNullOrEmpty(receiverType))
            {
                var memberSymbol = await ResolveMemberSymbolAsync(
                    ctx.ReleaseId, receiverType, ctx.Word, ct);
                if (memberSymbol is not null)
                {
                    return new ReferenceResolution(
                        new ResolutionTarget.MemberSymbol(memberSymbol.Id),
                        "variable-member");
                }

                var receiverObject = await ResolveObjectByNameAsync(
                    ctx.ReleaseId, receiverType, ct);
                if (receiverObject is not null)
                {
                    return new ReferenceResolution(
                        new ResolutionTarget.CatalogObject(receiverObject.Id),
                        "variable-type-fallback");
                }
            }
        }

        // Strategy: bare member click on a declaration in the current file.
        var localMember = await ResolveMemberSymbolInFileAsync(ctx.FileId, ctx.Word, ct);
        if (localMember is not null)
        {
            return new ReferenceResolution(
                new ResolutionTarget.MemberSymbol(localMember.Id),
                "local-member");
        }

        // Strategy: global-variable. The clicked token matches an
        // object-scope global variable on the file's owner object.
        // Catches the SalesDocCheckFactboxVisible-style click that
        // pre-refactor fell through to "no match" because globals
        // live in oe_module_variables, not oe_module_symbols. The
        // position columns added by step 2 are what lets
        // Go-to-definition land on the declaration line; this
        // strategy is the corresponding Find-references entry
        // point. See .design/al-reference-extractor-refactor.md
        // step 3b.
        var global = await TryResolveGlobalVariableAsync(ctx.FileId, ctx.Word, ct);
        if (global is not null)
        {
            return new ReferenceResolution(
                new ResolutionTarget.GlobalVariable(global.Id, global.OwnerObjectId),
                "global-variable");
        }

        return null;
    }

    private async Task<DbMatch?> ResolveObjectByNameAsync(
        int releaseId, string name, CancellationToken ct)
    {
        // Walk the visible release chain (child shadows parent) so a base
        // object referenced from a customer Release resolves to the ancestor
        // it actually lives in. See ChainObjectResolution.
        var hit = await ChainObjectResolution.ResolveObjectAsync(
            _db, releaseId, name, kind: null, objectId: null, ct);
        return hit is null ? null : new DbMatch(hit.Id);
    }

    /// <summary>
    /// Finds a member symbol (procedure / field / trigger) by name on the
    /// named owner object across the visible release chain. Picks the closest
    /// Release's match when the name exists at multiple depths.
    /// </summary>
    private async Task<DbMatch?> ResolveMemberSymbolAsync(
        int releaseId, string ownerName, string memberName, CancellationToken ct)
    {
        var symbolId = await ChainObjectResolution.ResolveMemberSymbolIdAsync(
            _db, releaseId, ownerName, memberName, ct);
        return symbolId is null ? null : new DbMatch(symbolId.Value);
    }

    /// <summary>
    /// Finds a member symbol by name inside the file the click landed on.
    /// Covers the case where the user clicks a procedure declaration or
    /// internal call inside the same file — no qualifier, name unambiguous
    /// at file scope.
    /// </summary>
    private async Task<DbMatch?> ResolveMemberSymbolInFileAsync(
        long fileId, string memberName, CancellationToken ct)
    {
        var loweredMember = memberName.ToLowerInvariant();
        return await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.Name.ToLower() == loweredMember)
            .OrderBy(s => s.Kind)
            .Select(s => new DbMatch(s.Id))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Strategy: extracted-ref — look up an extracted reference recorded
    /// on this file at the clicked line whose target name matches the
    /// clicked word, then resolve that reference's
    /// <c>(TargetAppId, Kind, ObjectId | Name)</c> triplet to a
    /// same-Release <c>OeModuleObject</c>.
    ///
    /// Beats the name-only fallback when a name collides across kinds
    /// (Microsoft ships pages and tables with identical setup names —
    /// "General Ledger Setup" exists as both a Table and a Page). The
    /// extractor already disambiguated at import time using the AL
    /// keyword context (<c>Record "X"</c> → table, <c>Page "X"</c> →
    /// page), so consulting that decision is more accurate than
    /// alphabetical kind sort.
    /// </summary>
    private async Task<DbMatch?> ResolveExtractedReferenceAtAsync(
        long fileId, int releaseId, int line, string word, CancellationToken ct)
    {
        var loweredWord = word.ToLowerInvariant();
        var refRow = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId)
            .Where(r => r.LineNumber == line)
            .Where(r => r.TargetObjectName.ToLower() == loweredWord)
            .Select(r => new
            {
                r.TargetAppId,
                r.TargetObjectKind,
                r.TargetObjectId,
                r.TargetObjectName,
            })
            .FirstOrDefaultAsync(ct);
        if (refRow is null) return null;

        // Resolve the reference's (kind, objectId|name) triplet across the
        // visible chain so a recorded reference to a base object lands on the
        // ancestor Release that defines it, not just the seed.
        var hit = await ChainObjectResolution.ResolveObjectAsync(
            _db, releaseId, refRow.TargetObjectName, refRow.TargetObjectKind, refRow.TargetObjectId, ct);
        return hit is null ? null : new DbMatch(hit.Id);
    }

    /// <summary>
    /// Looks up an object-scope global variable named
    /// <paramref name="word"/> on the file's owner object. Returns the
    /// variable id plus the owner object id; the caller's session
    /// query keys off the pair. Misses silently — globals are scoped
    /// per object, so a name that doesn't match any global is just
    /// a different identifier.
    /// </summary>
    private async Task<GlobalVarMatch?> TryResolveGlobalVariableAsync(
        long fileId, string word, CancellationToken ct)
    {
        var loweredWord = word.ToLowerInvariant();
        return await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.Object!.SourceFileId == fileId)
            .Where(v => v.Name.ToLower() == loweredWord)
            .OrderBy(v => v.Id)
            .Select(v => new GlobalVarMatch(v.Id, v.ObjectId))
            .FirstOrDefaultAsync(ct);
    }

    private sealed record DbMatch(long Id);
    private sealed record GlobalVarMatch(long Id, long OwnerObjectId);
}

/// <summary>
/// Input shape for <see cref="ReferenceResolver.ResolveAsync"/>. Caller
/// has already located the file (<see cref="FileId"/>,
/// <see cref="ReleaseId"/>, <see cref="FileContent"/>) and inspected
/// the click for the word + left-context (qualifier + operator) via
/// <see cref="AlGoToDefinitionLocator"/>.
/// </summary>
public sealed record ResolverContext(
    long FileId,
    int ReleaseId,
    int Line,
    int Column,
    string Word,
    string? LeftQualifier,
    string? LeftOperator,
    string FileContent);

/// <summary>
/// What the resolver decided the click points at, plus a <c>Reason</c>
/// tag that mirrors the strategy name in the resolver's chain. The tag
/// is logged at the call site so an operator reading the access log
/// can trace each click back to the strategy that resolved it.
/// </summary>
public sealed record ReferenceResolution(ResolutionTarget Target, string Reason);

/// <summary>
/// Discriminated union of what a click can resolve to. Each variant
/// carries the minimum the caller needs to mint a
/// <see cref="ReferenceSession"/>: a catalog object id or a member
/// symbol id. Future variants — global variables (step 3b), platform
/// virtual tables — slot in as additional members.
/// </summary>
public abstract record ResolutionTarget
{
    public sealed record CatalogObject(long ObjectId) : ResolutionTarget;
    public sealed record MemberSymbol(long SymbolId) : ResolutionTarget;
    public sealed record GlobalVariable(long VariableId, long OwnerObjectId) : ResolutionTarget;

    /// <summary>
    /// A bare identifier that matches a procedure-local (or object-scope
    /// <c>var</c>-block) declaration in the current file. Find-references
    /// on this target scopes to in-file occurrences instead of the
    /// underlying type's references — the user's mental model is
    /// "where else does this variable appear", not "every place this
    /// table is touched anywhere in the release". Go-to-definition on
    /// the same target navigates to the declaration line.
    /// </summary>
    public sealed record LocalVariable(long FileId, string Name) : ResolutionTarget;
}
