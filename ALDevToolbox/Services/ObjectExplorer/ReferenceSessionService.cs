using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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

    public ReferenceSessionService(
        IMemoryCache cache,
        ObjectExplorerService objectExplorer,
        Data.AppDbContext db)
    {
        _cache = cache;
        _objectExplorer = objectExplorer;
        _db = db;
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
    /// (line, column) in the supplied file, resolves it to a same-Release
    /// object (using the same locator <see cref="ObjectExplorerService.GoToDefinitionAsync"/>
    /// uses), and runs FindReferences on it. Returns null when nothing
    /// at the click position resolves to a known object.
    /// </summary>
    public async Task<ReferenceSession?> CreateAtPositionAsync(
        long fileId, int line, int column, string ownerKey, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.Content, ReleaseId = f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (meta is null) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;

        var word = click.Word;
        var target = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == meta.ReleaseId)
            .Where(o => o.Name == word)
            .OrderBy(o => o.Kind)
            .Select(o => new { o.Id })
            .FirstOrDefaultAsync(ct);
        if (target is null) return null;

        return await CreateFromSymbolAsync(target.Id, ownerKey, ct);
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
