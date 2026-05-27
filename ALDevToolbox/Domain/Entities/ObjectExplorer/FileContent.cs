namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Content-addressable store for <c>.al</c> source text, keyed by the SHA-256
/// hash of the content. One row per distinct blob, shared across every
/// <see cref="ModuleFile"/> — within a tenant, across modules/releases, and
/// across tenants — so identical first-party source (Microsoft Base App, …) is
/// stored once instead of once per organisation.
///
/// Deliberately carries **no** <c>organization_id</c> and is **never** added to
/// the multi-tenant query filter in <c>AppDbContext.OnModelCreating</c>. Tenant
/// isolation is preserved because the only path to a row is the
/// <see cref="ModuleFile.FileContent"/> navigation from an org-scoped
/// <see cref="ModuleFile"/> — the same pattern <c>PasswordResetToken</c> /
/// <c>UserPasskey</c> use. Never query this table as a root or expose it
/// directly; never add an org filter to it.
/// </summary>
public class FileContent
{
    /// <summary>SHA-256 of <see cref="Content"/> as hex (the content address). Primary key.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>UTF-8 source text, stored verbatim so the file viewer renders the same bytes.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Character length of <see cref="Content"/> (<c>string.Length</c> /
    /// Postgres <c>length()</c>), kept so <c>Release.SourceContentLength</c> can
    /// be summed without materialising the blob. Char count, not byte count.
    /// </summary>
    public int ContentLength { get; set; }

    /// <summary>Line count — a pure function of <see cref="Content"/>.</summary>
    public int LineCount { get; set; }
}
