namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Single source of truth for the mustache placeholders the generator
/// understands. The generator reads <see cref="Names"/> when matching tokens
/// during substitution; the admin UI reads <see cref="AvailableInAdminContent"/>
/// to render the hint shown above mustache-enabled editors. Keeping the list in
/// one place stops the doc-strings and the UI hint from drifting away from the
/// generator's actual switch.
/// </summary>
public static class MustacheVariableCatalog
{
    /// <summary>
    /// The full table of variables recognised by
    /// <see cref="Services.GenerationService"/>'s mustache substituter. Order is
    /// the order admins see in the hint — most-useful first, scoped/contextual
    /// last.
    /// </summary>
    public static readonly IReadOnlyList<MustacheVariable> All = new MustacheVariable[]
    {
        new("workspaceName", "Workspace display name as the user typed it (e.g. \"Acme Customer\").", AvailableInAdminContent: true),
        new("shortName", "Workspace name with whitespace stripped (e.g. \"AcmeCustomer\"). Used in filenames.", AvailableInAdminContent: true),
        new("publisher", "Organisation publisher from the configuration defaults.", AvailableInAdminContent: true),
        new("extension_prefix", "Extension prefix from the New Workspace form.", AvailableInAdminContent: true),
        new("affix", "Template affix when the template's affix type is not 'None'; empty otherwise.", AvailableInAdminContent: true),
        new("name", "Resolved name of the current extension (per-file context only).", AvailableInAdminContent: false),
        new("moduleName", "Module name when generating from a catalogue module clone.", AvailableInAdminContent: false),
        new("namespace", "Folder path of the current AL file, dots-separated (per-file context only).", AvailableInAdminContent: false),
        new("guid", "Fresh GUID generated on every substitution — avoid in admin-edited files; the file would change on every generation.", AvailableInAdminContent: false),
    };

    /// <summary>Subset surfaced in the admin-facing hint above mustache-enabled editors.</summary>
    public static IEnumerable<MustacheVariable> ForAdminContent =>
        All.Where(v => v.AvailableInAdminContent);

    /// <summary>All recognised names — used by the generator and tested for parity with the catalogue.</summary>
    public static IReadOnlySet<string> Names { get; } =
        All.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
}

/// <summary>One entry in <see cref="MustacheVariableCatalog"/>.</summary>
/// <param name="Name">The placeholder name (without the surrounding braces).</param>
/// <param name="Caption">One-line description shown in the admin hint.</param>
/// <param name="AvailableInAdminContent">
/// True when the variable resolves to a stable, useful value in
/// admin-edited org-wide files. False for per-file or volatile substitutions
/// (e.g. <c>{{guid}}</c>) that would produce churn if embedded in
/// always-included content or the workspace settings JSON.
/// </param>
public sealed record MustacheVariable(string Name, string Caption, bool AvailableInAdminContent);
