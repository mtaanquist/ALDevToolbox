using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;

namespace ALDevToolbox.Services;

/// <summary>
/// Bridge between the persisted <see cref="RuntimeTemplate"/> shape and the
/// <c>template.toml</c> document format. The TOML schema is described in
/// <c>.design/templates-and-seeding.md</c>.
/// </summary>
/// <remarks>
/// This class is in transitional state after Issue #54 introduced the unified
/// <c>[[extensions]]</c> model: the old <c>[[folders]]</c> /
/// <c>[[module_folders]]</c> shape no longer maps onto the schema, and the
/// rewrite around recursive folders + per-extension dependencies is pending.
/// The public API surface is preserved so the admin TOML editor compiles; every
/// entry-point throws <see cref="NotImplementedException"/> until the follow-on
/// implementation lands. Once it does, this comment block goes too.
/// </remarks>
public static class TemplateTomlMapper
{
    private const string PendingMessage =
        "TemplateTomlMapper has not been migrated to the unified-extensions schema. " +
        "See Issue #54 follow-up; the new shape is described in .design/templates-and-seeding.md.";

    public static string ToToml(RuntimeTemplate template) =>
        throw new NotImplementedException(PendingMessage);

    public static TemplateInput FromToml(string toml, bool deprecated) =>
        throw new NotImplementedException(PendingMessage);

    public static string NormalizeRuntimeValue(string toml) => toml;

    public static string BlankToml() =>
        throw new NotImplementedException(PendingMessage);

    /// <summary>
    /// Folder paths historically pre-declared on a new template. Retained for
    /// the admin form's blank-template seed list — the new editor will read
    /// from a per-extension layout once the rewrite lands.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFolderPaths = new[]
    {
        "libs",
        "permissionsets",
        "Translations",
    };
}
