namespace ALDevToolbox.Services;

/// <summary>
/// Per-deployment cache-buster token appended to static-asset URLs that
/// can't rely on file-content hashing (notably ES modules referenced from
/// inline scripts or imported by URL). Computed once at startup and stays
/// stable for the life of the process, so browser caching still works
/// between requests but a fresh deploy always invalidates.
/// </summary>
public sealed class CacheBust
{
    public string Token { get; } = Guid.NewGuid().ToString("N").AsSpan(0, 8).ToString();
}
