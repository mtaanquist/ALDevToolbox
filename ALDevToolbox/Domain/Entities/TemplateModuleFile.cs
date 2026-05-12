namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One seeded file inside a <see cref="TemplateModuleFolder"/>. Same shape and
/// substitution rules as <see cref="TemplateFile"/>, but emitted only into
/// generated module extensions (not Core).
/// </summary>
public class TemplateModuleFile
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the folder's value.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning module folder. Cascade-deleted when the folder is removed.</summary>
    public int TemplateModuleFolderId { get; set; }
    public TemplateModuleFolder? Folder { get; set; }

    /// <summary>Position within the folder's file list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Relative path inside the folder, using forward slashes (e.g.
    /// <c>Setup.Page.al</c> or <c>nested/Util.Codeunit.al</c>). No leading
    /// slash, no <c>..</c> segments. Unique per folder.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Raw file body. Written into the generated ZIP after mustache
    /// substitution for <c>.al</c> files; written verbatim for everything else.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Same semantics as <see cref="TemplateFile.IsExample"/> — files flagged
    /// true are skipped when the end user clears "Include example AL files".
    /// </summary>
    public bool IsExample { get; set; }
}
