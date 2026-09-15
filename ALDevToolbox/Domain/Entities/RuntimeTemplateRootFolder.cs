namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A folder the generator creates at the workspace root that holds nothing —
/// the symbol cache an AL build writes into (<c>.alpackages</c>), an assets
/// folder that should exist before anything is put in it, a team's
/// <c>docs/</c> convention. Extensions are the only other source of root-level
/// folders, and every one of those carries an <c>app.json</c>, so a template
/// had no way to declare an empty one before this table existed.
/// <para>
/// Content is deliberately out of scope: a folder that needs files is an
/// <see cref="OrganizationFile"/> whose path nests (the ruleset lives at
/// <c>.assets/rulesets/Company.ruleset.json</c> that way). These rows carry a
/// path and nothing else, and the ZIP writes a placeholder inside each so the
/// directory survives being committed to a repository.
/// See <c>.design/generation-engine.md</c>.
/// </para>
/// </summary>
public class RuntimeTemplateRootFolder
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int RuntimeTemplateId { get; set; }
    public RuntimeTemplate? RuntimeTemplate { get; set; }

    /// <summary>
    /// Workspace-root-relative path with forward slashes (e.g.
    /// <c>.alpackages</c> or <c>docs/decisions</c>). No leading slash and no
    /// <c>..</c> segments; a leading dot on a segment is legal and expected.
    /// Unique per template.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Position in the admin's reorderable list inside the template editor.</summary>
    public int Ordering { get; set; }
}
