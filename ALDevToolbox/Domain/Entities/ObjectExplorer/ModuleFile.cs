namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One <c>.al</c> source file kept from a <see cref="Module"/>'s archive. Non-source artifacts
/// (<c>.rdlc</c> layouts, <c>.xlf</c> translations, images) are deliberately not stored — see
/// the storage-policy table in <c>.design/object-explorer.md</c>. Source comes from the
/// embedded <c>src/</c> tree inside the <c>.app</c> when <c>IncludeSourceInSymbolFile=true</c>,
/// otherwise from the paired <c>.Source.zip</c>.
/// </summary>
public class ModuleFile
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public long ModuleId { get; set; }
    public Module? Module { get; set; }

    /// <summary>Path relative to the module's source root (e.g. <c>src/Codeunits/Agent.Codeunit.al</c>).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>UTF-8 source text, stored verbatim so the file viewer renders the same bytes.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="Content"/> as hex. Powers cross-Release diffing.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Denormalised line count for the browser table.</summary>
    public int LineCount { get; set; }
}
