namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Where in the generated workspace an <see cref="Domain.Entities.OrganizationFile"/>
/// row gets emitted. The same library serves both shapes — admins curate
/// every always-included file in one place; the per-template join controls
/// opt-in; this flag controls placement.
/// </summary>
/// <remarks>
/// Added when AppSourceCop.json was moved off the structured
/// <c>AppSourceCopSettings</c> column on RuntimeTemplate. The user-facing
/// rationale: avoid "magic" entity fields the generator special-cases.
/// Treat every file the same way — its content, mustache flag, and scope
/// are all admin-curated and round-trip through the same editor.
/// </remarks>
public enum OrganizationFileScope
{
    /// <summary>
    /// Emitted once at the workspace root. Mustache context is built once
    /// per generation; <c>{{name}}</c> resolves to the workspace name and
    /// <c>{{namespace}}</c> is empty.
    /// </summary>
    WorkspaceRoot = 0,

    /// <summary>
    /// Duplicated into every extension folder (the template's required and
    /// ticked-optional extensions plus every selected module clone).
    /// Mustache context is built per-extension so <c>{{name}}</c> resolves
    /// to that extension's rendered name. Use for files that AL conventions
    /// place per-extension — AppSourceCop.json, per-extension .editorconfig
    /// overrides, etc.
    /// </summary>
    EveryExtension = 1,
}
