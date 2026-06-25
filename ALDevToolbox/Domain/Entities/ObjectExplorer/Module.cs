namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One <c>.app</c> file inside a <see cref="Release"/>. The AppId / Name / Publisher / Version
/// triplet identifies a specific compiled module across the entire BC ecosystem; the same AppId
/// at different Versions across two Releases is how cross-Release evolution queries hang
/// together. Reference targets resolve to this row at query time via the recursive CTE on
/// <see cref="Release.ParentReleaseId"/>.
///
/// Name-collision note: <c>ALDevToolbox.Domain.Entities.Module</c> (the template-catalogue
/// module) is a different entity. C# distinguishes them by namespace; in files that touch
/// both, use a <c>using OeModule = ...ObjectExplorer.Module;</c> alias.
/// </summary>
public class Module
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ReleaseId { get; set; }
    public Release? Release { get; set; }

    /// <summary>The <c>App.Id</c> attribute from <c>NavxManifest.xml</c> — stable across versions.</summary>
    public Guid AppId { get; set; }

    /// <summary>The <c>App.Name</c> attribute (e.g. <c>"Base Application"</c>, <c>"DK Core"</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The <c>App.Publisher</c> attribute (e.g. <c>"Microsoft"</c>, <c>"Continia"</c>).</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>The <c>App.Version</c> attribute, full 4-part (e.g. <c>"25.18.48229.0"</c>).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The <c>App.Target</c> attribute — <c>"Cloud"</c>, <c>"OnPrem"</c>, or null.</summary>
    public string? Target { get; set; }

    /// <summary>The <c>App.Runtime</c> attribute (e.g. <c>"14.0"</c>).</summary>
    public string? Runtime { get; set; }

    /// <summary>
    /// Test app — landed in a folder matching <c>Test</c> / <c>Test Library</c> /
    /// <c>testframework/</c> on the ingested archive. Hidden by default in the UI;
    /// SiteAdmins can flip a toggle to see them.
    /// </summary>
    public bool IsTest { get; set; }

    /// <summary>
    /// Microsoft's <c>_Exclude_</c> filename marker — platform-internal apps that ship on the
    /// DVD but aren't part of the public product. Surfaces in the UI with a badge; otherwise
    /// treated identically.
    /// </summary>
    public bool IsInternal { get; set; }

    /// <summary>
    /// Translation-only app (e.g. <c>"Danish language (Denmark)"</c>). Ingested as a bare row
    /// so the dependency graph is closed; no <see cref="ModuleObject"/>s extracted.
    /// </summary>
    public bool IsLanguagePack { get; set; }

    /// <summary>
    /// The <c>Dependencies</c> block from the manifest, serialised as a JSON array of
    /// <c>{ "id": "...", "name": "...", "publisher": "...", "version": "..." }</c> entries.
    /// Stored as jsonb. Used by the third-party upload UI to infer parent Release.
    /// </summary>
    public string DependenciesJson { get; set; } = "[]";

    /// <summary>
    /// Denormalised number of entries in <see cref="DependenciesJson"/>, stamped at
    /// import. The objects grid's default order uses it to float foundational apps
    /// (System / Base Application — fewest dependencies) ahead of partner / project
    /// extensions without parsing JSON per row.
    /// </summary>
    public int DependencyCount { get; set; }

    /// <summary>SHA-256 of the original <c>.app</c> bytes. Lets the importer skip identical re-uploads.</summary>
    public string? AppFileHash { get; set; }

    /// <summary>
    /// FK into the shared <see cref="FileContent"/> store holding this module's
    /// raw <c>SymbolReference.json</c>, or <see langword="null"/> when it wasn't
    /// captured (the per-import "store symbol reference" option was off, or the
    /// app shipped no symbol file). Opt-in because a base-app symbol file is
    /// tens of MB; kept for debugging resolver errors and downloadable via the
    /// <c>download_symbol_reference</c> MCP tool.
    /// </summary>
    public string? SymbolReferenceContentHash { get; set; }

    /// <summary>Navigation to the stored <c>SymbolReference.json</c> text; null when not captured.</summary>
    public FileContent? SymbolReferenceContent { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<ModuleFile> Files { get; set; } = new List<ModuleFile>();
    public ICollection<ModuleObject> Objects { get; set; } = new List<ModuleObject>();
    public ICollection<ModuleSymbol> Symbols { get; set; } = new List<ModuleSymbol>();
    public ICollection<ModuleVariable> Variables { get; set; } = new List<ModuleVariable>();
    public ICollection<ModuleReference> References { get; set; } = new List<ModuleReference>();
}
