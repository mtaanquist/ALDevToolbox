namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One file inside a <see cref="Recipe"/>. Stored as UTF-8 text.
///
/// Recipes carry a folder structure: <see cref="RelativePath"/> holds the
/// directory the file lives in (empty = root), and <see cref="FileName"/>
/// holds just the basename. The ZIP download joins the two with <c>/</c>
/// when emitting entries so <c>ZipArchive</c> materialises the directories
/// automatically.
/// </summary>
public class RecipeFile
{
    public int Id { get; set; }

    /// <summary>Denormalised owning organisation; mirrors the recipe's value.</summary>
    public int OrganizationId { get; set; }

    /// <summary>Owning recipe. Cascade-deleted when the recipe is removed.</summary>
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>Position within the recipe's file list.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// Folder path inside the ZIP. Empty = root. Validated as a relative
    /// path: no leading or trailing <c>/</c>, no <c>..</c> or <c>.</c>
    /// segments, no control characters; max 8 segments, max 260 chars total.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Flat file name (e.g. <c>DocAttachListFactboxSub.Codeunit.al</c>). No slashes.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Raw file body. Rendered verbatim in the browser with a copy-to-clipboard button.</summary>
    public string Content { get; set; } = string.Empty;
}
