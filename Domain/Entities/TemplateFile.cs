namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One seeded file inside a <see cref="TemplateFolder"/>. Stored as UTF-8 text;
/// binary template assets are out of scope per <c>.design/templates-and-seeding.md</c>.
/// Mustache substitution runs at generation time, not at write time, so the
/// <see cref="Content"/> column holds the raw template body verbatim.
/// </summary>
public class TemplateFile
{
    public int Id { get; set; }

    /// <summary>Owning folder. Cascade-deleted when the folder is removed.</summary>
    public int TemplateFolderId { get; set; }
    public TemplateFolder? Folder { get; set; }

    /// <summary>Position within the folder's file list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Relative path inside the folder, using forward slashes (e.g.
    /// <c>AppInstall.Codeunit.al</c> or <c>nested/Util.Codeunit.al</c>).
    /// No leading slash, no <c>..</c> segments. Unique per folder.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Raw file body. Written into the generated ZIP after mustache
    /// substitution for <c>.al</c> files; written verbatim for everything else.
    /// </summary>
    public string Content { get; set; } = string.Empty;
}
