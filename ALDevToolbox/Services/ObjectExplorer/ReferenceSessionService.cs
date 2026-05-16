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
    /// Mints a session by clicking position: looks up the word at
    /// (line, column) in the supplied file and resolves it to a
    /// same-Release object. Tries three strategies in order:
    /// <list type="number">
    ///   <item>The word itself matches an object name (case-insensitive).</item>
    ///   <item>The click was qualified (<c>myCust.Insert</c>) — resolve the
    ///         qualifier's declared type and look that up as the object.
    ///         Procedure-level references aren't tracked yet, so the user
    ///         gets references to the receiver type instead.</item>
    /// </list>
    /// Returns null when neither strategy resolves a known object.
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

        // Strategy 1: direct object match (case-insensitive — AL is too).
        var target = await ResolveObjectByNameAsync(meta.ReleaseId, click.Word, ct);

        // Strategy 2: variable-receiver fallback for `myCust.Insert` style
        // clicks. We track references at the object level, not the
        // method-call level, so the closest correct answer is "references
        // to the variable's declared type".
        string? resolvedVia = target is null ? null : "name";
        string? receiverType = null;
        if (target is null && click.LeftContext.Operator == "."
            && !string.IsNullOrEmpty(click.LeftContext.Qualifier))
        {
            receiverType = Services.Al.AlGoToDefinitionLocator
                .ResolveVariableType(meta.Content, click.LeftContext.Qualifier!);
            if (!string.IsNullOrEmpty(receiverType))
            {
                target = await ResolveObjectByNameAsync(meta.ReleaseId, receiverType, ct);
                if (target is not null) resolvedVia = "variable-type";
            }
        }

        if (target is null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Path='{Path}' Word='{Word}' Qualifier='{Qual}' Receiver='{Recv}' — no object match.",
                fileId, meta.Path, click.Word, click.LeftContext.Qualifier, receiverType);
            return null;
        }

        _logger.LogInformation(
            "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via={Strategy} to ObjectId={ObjectId}.",
            fileId, click.Word, resolvedVia, target.Id);

        return await CreateFromSymbolAsync(target.Id, ownerKey, ct);
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

    private sealed record ObjectMatch(long Id);

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
