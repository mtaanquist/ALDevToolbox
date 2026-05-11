namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One file inside a <see cref="Snippet"/>. Stored as UTF-8 text. File
/// names are flat (no path separators) — by design snippets don't carry
/// folder structure; the user copies each file into wherever their AL
/// project organises that kind of object.
/// </summary>
public class SnippetFile
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the snippet's value.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning snippet. Cascade-deleted when the snippet is removed.</summary>
    public int SnippetId { get; set; }
    public Snippet? Snippet { get; set; }

    /// <summary>Position within the snippet's file list.</summary>
    public int Ordering { get; set; }

    /// <summary>Flat file name (e.g. <c>DocAttachListFactboxSub.Codeunit.al</c>). No slashes.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Raw file body. Rendered verbatim in the browser with a copy-to-clipboard button.</summary>
    public string Content { get; set; } = string.Empty;
}
