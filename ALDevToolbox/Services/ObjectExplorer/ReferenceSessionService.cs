using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// In-memory cache for active "Find references" searches. A token minted
/// here lives in the URL bar via <c>?refSet=</c> and lets the source
/// viewer keep the result list visible while the user clicks through
/// references in different files. Sessions are scoped per user (no
/// cross-user reuse) and expire after a sliding 30 minutes — refreshed
/// each time the user lands on a page consuming the token.
///
/// State is process-local on purpose: per
/// <c>.design/architecture.md</c> we don't run Redis, and a restart
/// just means the user re-triggers Find references — no data loss.
/// </summary>
public sealed class ReferenceSessionService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;
    private readonly ObjectExplorerService _objectExplorer;
    private readonly Data.AppDbContext _db;
    private readonly ILogger<ReferenceSessionService> _logger;

    public ReferenceSessionService(
        IMemoryCache cache,
        ObjectExplorerService objectExplorer,
        Data.AppDbContext db,
        ILogger<ReferenceSessionService> logger)
    {
        _cache = cache;
        _objectExplorer = objectExplorer;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Mints a session keyed off a source-object id. Resolves the object's
    /// owning Module to get the <c>AppId</c> + <c>ReleaseId</c> for the
    /// underlying <see cref="ObjectExplorerService.FindReferencesAsync"/>
    /// call. Returns null when the object id is unknown.
    /// </summary>
    public async Task<ReferenceSession?> CreateFromSymbolAsync(
        long objectId, string ownerKey, CancellationToken ct = default)
    {
        var head = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => new
            {
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
                ReleaseId = o.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;

        var query = new FindReferencesQuery(
            TargetAppId: head.AppId,
            TargetObjectKind: head.Kind,
            TargetObjectId: head.ObjectId,
            TargetObjectName: head.Name);

        var results = await _objectExplorer.FindReferencesAsync(head.ReleaseId, query, ct);
        var label = head.ObjectId is { } oid
            ? $"references to {head.Kind} {oid} {head.Name}"
            : $"references to {head.Kind} {head.Name}";

        return Store(label, head.ReleaseId, results, ownerKey);
    }

    /// <summary>
    /// Member-scoped variant: mints a session for a specific procedure /
    /// field / trigger on an owner object. Used by the outline right-click
    /// path (where the symbol id is known directly) and by the at-position
    /// path when the click resolves to a member.
    /// </summary>
    public async Task<ReferenceSession?> CreateFromMemberSymbolAsync(
        long symbolId, string ownerKey, CancellationToken ct = default)
    {
        // Flat projection so we don't depend on a child navigation that
        // wasn't included. Projecting `Owner = s.Object!` materialised
        // just the object row; reading head.Owner.Module then threw an
        // NRE because EF never loaded the Module navigation property.
        // Pulling every column scalar dodges the navigation entirely.
        var head = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                s.Kind,
                s.Name,
                s.Signature,
                OwnerKind = s.Object!.Kind,
                OwnerObjectId = s.Object!.ObjectId,
                OwnerName = s.Object!.Name,
                OwnerAppId = s.Object!.Module!.AppId,
                ReleaseId = s.Object!.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;

        var query = new FindReferencesQuery(
            TargetAppId: head.OwnerAppId,
            TargetObjectKind: head.OwnerKind,
            TargetObjectId: head.OwnerObjectId,
            TargetObjectName: head.OwnerName,
            TargetMemberName: head.Name,
            TargetMemberKind: head.Kind);

        var results = await _objectExplorer.FindReferencesForSymbolAsync(
            head.ReleaseId, query, ct);

        var sigPart = string.IsNullOrEmpty(head.Signature) ? "" : head.Signature;
        var label = head.OwnerObjectId is { } oid
            ? $"references to {head.Kind} {head.OwnerKind} {oid} {head.OwnerName}.{head.Name}{sigPart}"
            : $"references to {head.Kind} {head.OwnerKind} {head.OwnerName}.{head.Name}{sigPart}";

        return Store(label, head.ReleaseId, results, ownerKey);
    }

    /// <summary>
    /// Mints a session by clicking position: looks up the word at
    /// (line, column) in the supplied file and resolves it to a
    /// same-Release target. Tries four strategies in order:
    /// <list type="number">
    ///   <item>The word matches an object name (case-insensitive) →
    ///         object-scoped find.</item>
    ///   <item>The click is qualified (<c>myCust.Insert</c>) — resolve the
    ///         qualifier's declared type, look up the member symbol on that
    ///         type → member-scoped find.</item>
    ///   <item>The word matches a symbol declaration in the current file
    ///         (procedure / field / trigger on this file's owner) →
    ///         member-scoped find on the current owner.</item>
    ///   <item>Fall back to step 2 with the receiver-type itself as the
    ///         object — handy when the member isn't recorded yet (phase 2
    ///         schema gap).</item>
    /// </list>
    /// Returns null when nothing at the click resolves.
    /// </summary>
    public async Task<ReferenceSession?> CreateAtPositionAsync(
        long fileId, int line, int column, string ownerKey, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.Content, ReleaseId = f.Module!.ReleaseId, f.Path })
            .SingleOrDefaultAsync(ct);
        if (meta is null)
        {
            _logger.LogInformation("FindRefsAtPosition FileId={FileId} not found.", fileId);
            return null;
        }

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word))
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Line={Line} Col={Col} no token at cursor.",
                fileId, line, column);
            return null;
        }

        // Strategy 0: an extracted reference at this exact (file, line)
        // already records what the token resolves to (kind + name).
        // Use it before the name-only DB lookup so token-level
        // disambiguation survives: a `Record "General Ledger Setup"`
        // type name resolves to the Table even though a Page of the
        // same name also exists in the release.
        var precise = await ResolveExtractedReferenceAtAsync(
            fileId, meta.ReleaseId, line, click.Word, ct);
        if (precise is not null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via=extracted-ref to ObjectId={ObjectId} Kind={Kind}.",
                fileId, click.Word, precise.Id, precise.Kind);
            return await CreateFromSymbolAsync(precise.Id, ownerKey, ct);
        }

        // Strategy 1: direct object match (case-insensitive — AL is too).
        var objectMatch = await ResolveObjectByNameAsync(meta.ReleaseId, click.Word, ct);
        if (objectMatch is not null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via=object-name to ObjectId={ObjectId}.",
                fileId, click.Word, objectMatch.Id);
            return await CreateFromSymbolAsync(objectMatch.Id, ownerKey, ct);
        }

        // Strategy 2: qualified `var.Member`. Look up the variable's declared
        // type, then find the member symbol on that type.
        if (click.LeftContext.Operator == "."
            && !string.IsNullOrEmpty(click.LeftContext.Qualifier))
        {
            var receiverType = Services.Al.AlGoToDefinitionLocator
                .ResolveVariableType(meta.Content, click.LeftContext.Qualifier!);
            if (!string.IsNullOrEmpty(receiverType))
            {
                var memberSymbol = await ResolveMemberSymbolAsync(
                    meta.ReleaseId, receiverType, click.Word, ct);
                if (memberSymbol is not null)
                {
                    _logger.LogInformation(
                        "FindRefsAtPosition FileId={FileId} Word='{Word}' Qualifier='{Qual}' resolved-via=variable-member to SymbolId={SymbolId}.",
                        fileId, click.Word, click.LeftContext.Qualifier, memberSymbol.Id);
                    return await CreateFromMemberSymbolAsync(memberSymbol.Id, ownerKey, ct);
                }

                // Strategy 4 (member symbol wasn't found): fall back to
                // object-scoped find on the receiver type. The user gets
                // "references to Customer" instead of nothing.
                var receiverObject = await ResolveObjectByNameAsync(
                    meta.ReleaseId, receiverType, ct);
                if (receiverObject is not null)
                {
                    _logger.LogInformation(
                        "FindRefsAtPosition FileId={FileId} Word='{Word}' Qualifier='{Qual}' resolved-via=variable-type-fallback to ObjectId={ObjectId}.",
                        fileId, click.Word, click.LeftContext.Qualifier, receiverObject.Id);
                    return await CreateFromSymbolAsync(receiverObject.Id, ownerKey, ct);
                }
            }
        }

        // Strategy 3: bare member click on a procedure/field declared in
        // the current file. Resolve by walking the file's owner objects
        // and finding a matching symbol.
        var localMember = await ResolveMemberSymbolInFileAsync(fileId, click.Word, ct);
        if (localMember is not null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via=local-member to SymbolId={SymbolId}.",
                fileId, click.Word, localMember.Id);
            return await CreateFromMemberSymbolAsync(localMember.Id, ownerKey, ct);
        }

        _logger.LogInformation(
            "FindRefsAtPosition FileId={FileId} Path='{Path}' Word='{Word}' Qualifier='{Qual}' — no match.",
            fileId, meta.Path, click.Word, click.LeftContext.Qualifier);
        return null;
    }

    private async Task<ObjectMatch?> ResolveObjectByNameAsync(
        int releaseId, string name, CancellationToken ct)
    {
        // AL is case-insensitive; the schema stores names with original
        // casing, so query lower-cased on both sides.
        var lowered = name.ToLowerInvariant();
        return await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Where(o => o.Name.ToLower() == lowered)
            .OrderBy(o => o.Kind)
            .Select(o => new ObjectMatch(o.Id))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Finds a member symbol (procedure / field / trigger) by name on the
    /// named owner object in the supplied release. Picks the first match
    /// when multiple kinds share the name (rare but possible — e.g. a
    /// procedure and a field with identical names).
    /// </summary>
    private async Task<ObjectMatch?> ResolveMemberSymbolAsync(
        int releaseId, string ownerName, string memberName, CancellationToken ct)
    {
        var loweredOwner = ownerName.ToLowerInvariant();
        var loweredMember = memberName.ToLowerInvariant();
        return await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == releaseId)
            .Where(s => s.Object!.Name.ToLower() == loweredOwner)
            .Where(s => s.Name.ToLower() == loweredMember)
            .OrderBy(s => s.Kind)
            .Select(s => new ObjectMatch(s.Id))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Finds a member symbol by name inside the file the click landed on.
    /// Covers the case where the user clicks a procedure declaration or
    /// internal call inside the same file — no qualifier, name unambiguous
    /// at file scope.
    /// </summary>
    private async Task<ObjectMatch?> ResolveMemberSymbolInFileAsync(
        long fileId, string memberName, CancellationToken ct)
    {
        var loweredMember = memberName.ToLowerInvariant();
        return await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.Name.ToLower() == loweredMember)
            .OrderBy(s => s.Kind)
            .Select(s => new ObjectMatch(s.Id))
            .FirstOrDefaultAsync(ct);
    }

    private sealed record ObjectMatch(long Id, string? Kind = null);

    /// <summary>
    /// Strategy 0 of <see cref="CreateAtPositionAsync"/>: look up an
    /// extracted reference recorded on this file at the clicked line
    /// whose target name matches the clicked word, then resolve that
    /// reference's <c>(TargetAppId, Kind, ObjectId | Name)</c> triplet
    /// to a same-Release <c>OeModuleObject</c>.
    ///
    /// Beats the name-only fallback when a name collides across kinds
    /// (Microsoft ships pages and tables with identical setup names —
    /// "General Ledger Setup" exists as both a Table and a Page). The
    /// extractor already disambiguated at import time using the AL
    /// keyword context (<c>Record "X"</c> → table, <c>Page "X"</c> →
    /// page), so consulting that decision is more accurate than
    /// alphabetical kind sort.
    /// </summary>
    private async Task<ObjectMatch?> ResolveExtractedReferenceAtAsync(
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

        var loweredName = refRow.TargetObjectName.ToLowerInvariant();
        var loweredKind = refRow.TargetObjectKind.ToLowerInvariant();
        var query = _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId)
            .Where(o => o.Kind.ToLower() == loweredKind)
            .Where(o => o.Name.ToLower() == loweredName);
        if (refRow.TargetObjectId is { } targetObjId)
        {
            query = query.Where(o => o.ObjectId == targetObjId);
        }
        return await query
            .Select(o => new ObjectMatch(o.Id, o.Kind))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Retrieves a previously minted session by token. Returns null when
    /// the token is unknown, expired, or owned by a different user — the
    /// caller should hide the panel in that case (token in URL stale).
    /// </summary>
    public ReferenceSession? Get(string token, string ownerKey)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_cache.TryGetValue(CacheKey(token), out CachedSession? cached) || cached is null) return null;
        if (!string.Equals(cached.OwnerKey, ownerKey, StringComparison.Ordinal)) return null;
        return cached.Session;
    }

    private ReferenceSession Store(
        string label, int releaseId, IReadOnlyList<ReferenceMatch> results, string ownerKey)
    {
        var token = Guid.NewGuid().ToString("N");
        var session = new ReferenceSession(token, label, releaseId, results);
        _cache.Set(
            CacheKey(token),
            new CachedSession(session, ownerKey),
            new MemoryCacheEntryOptions { SlidingExpiration = SessionTtl });
        return session;
    }

    private static string CacheKey(string token) => "oe-refs:" + token;

    private sealed record CachedSession(ReferenceSession Session, string OwnerKey);
}
