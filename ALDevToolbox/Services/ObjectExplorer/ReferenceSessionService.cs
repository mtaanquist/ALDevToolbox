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
    private readonly ReferenceResolver _resolver;
    private readonly ILogger<ReferenceSessionService> _logger;

    public ReferenceSessionService(
        IMemoryCache cache,
        ObjectExplorerService objectExplorer,
        Data.AppDbContext db,
        ReferenceResolver resolver,
        ILogger<ReferenceSessionService> logger)
    {
        _cache = cache;
        _objectExplorer = objectExplorer;
        _db = db;
        _resolver = resolver;
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
    /// (line, column) in the supplied file, then delegates the
    /// "what does this token point at?" question to
    /// <see cref="ReferenceResolver"/> — the single source of truth for
    /// click-time resolution shared with the extractor side (see
    /// <c>.design/al-reference-extractor-refactor.md</c> step 3).
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

        var resolution = await _resolver.ResolveAsync(new ResolverContext(
            FileId: fileId,
            ReleaseId: meta.ReleaseId,
            Line: line,
            Column: column,
            Word: click.Word,
            LeftQualifier: click.LeftContext.Qualifier,
            LeftOperator: click.LeftContext.Operator,
            FileContent: meta.Content), ct);
        if (resolution is null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Path='{Path}' Word='{Word}' Qualifier='{Qual}' — no match.",
                fileId, meta.Path, click.Word, click.LeftContext.Qualifier);
            return null;
        }

        switch (resolution.Target)
        {
            case ResolutionTarget.CatalogObject catalog:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via={Reason} to ObjectId={ObjectId}.",
                    fileId, click.Word, resolution.Reason, catalog.ObjectId);
                return await CreateFromSymbolAsync(catalog.ObjectId, ownerKey, ct);
            case ResolutionTarget.MemberSymbol member:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' Qualifier='{Qual}' resolved-via={Reason} to SymbolId={SymbolId}.",
                    fileId, click.Word, click.LeftContext.Qualifier, resolution.Reason, member.SymbolId);
                return await CreateFromMemberSymbolAsync(member.SymbolId, ownerKey, ct);
            default:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} resolved to unknown target {Target}.",
                    fileId, resolution.Target);
                return null;
        }
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
