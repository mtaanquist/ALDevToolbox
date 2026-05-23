using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services;

/// <summary>
/// Pure, DB-free validation of a <see cref="TemplateAuthoring"/> payload: JSON
/// column parsing and the recursive extension / folder / dependency rules.
/// Extracted from <see cref="TemplateService"/> so the rule set can be read and
/// tested without standing up the service. The DB-bound checks (key
/// uniqueness, FK resolution) stay on the service and call into these.
/// </summary>
internal static class TemplateValidation
{
    private static readonly Regex PathSegmentRegex = new(@"^[^/\\\s][^/\\]*[^/\\\s]$|^[^/\\\s]$", RegexOptions.Compiled);
    private static readonly Regex ExtensionPathRegex = new(@"^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Deserialises the three JSON columns (defaults, app-source-cop, optional
    /// code-workspace overlay). Field-keyed errors are collected; deserialised
    /// shapes are returned for the caller to assign onto the entity.
    /// </summary>
    public static (TemplateDefaults Defaults, AppSourceCopSettings AppSourceCop) ParseJsonOverrides(
        TemplateAuthoring input, Dictionary<string, string> errors)
    {
        TemplateDefaults defaults = new();
        try
        {
            defaults = string.IsNullOrWhiteSpace(input.DefaultsJson)
                ? new TemplateDefaults()
                : JsonSerializer.Deserialize<TemplateDefaults>(input.DefaultsJson, PersistenceJson.Options) ?? new TemplateDefaults();
        }
        catch (JsonException ex)
        {
            errors[nameof(input.DefaultsJson)] = $"Defaults JSON is invalid: {ex.Message}";
        }

        AppSourceCopSettings appSourceCop = new();
        try
        {
            appSourceCop = string.IsNullOrWhiteSpace(input.AppSourceCopJson)
                ? new AppSourceCopSettings()
                : JsonSerializer.Deserialize<AppSourceCopSettings>(input.AppSourceCopJson, PersistenceJson.Options) ?? new AppSourceCopSettings();
        }
        catch (JsonException ex)
        {
            errors[nameof(input.AppSourceCopJson)] = $"AppSourceCop JSON is invalid: {ex.Message}";
        }

        // Optional per-template workspace JSON: empty / whitespace is treated
        // as "inherit org base", anything else must parse to a JSON object.
        if (!string.IsNullOrWhiteSpace(input.CodeWorkspaceJson))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(input.CodeWorkspaceJson);
                if (node is not System.Text.Json.Nodes.JsonObject)
                {
                    errors[nameof(input.CodeWorkspaceJson)] =
                        "Workspace JSON template must be a JSON object (e.g. { \"settings\": {...} }).";
                }
            }
            catch (JsonException ex)
            {
                errors[nameof(input.CodeWorkspaceJson)] = $"Workspace JSON template is invalid: {ex.Message}";
            }
        }

        return (defaults, appSourceCop);
    }

    /// <summary>
    /// Walks every <c>[[extensions]]</c> entry and checks: extension <c>path</c>
    /// non-empty + unique + filesystem-safe; <c>name</c> template non-empty;
    /// id-range pair both-or-neither; recursive folder/file paths are
    /// single-segment + sibling-unique; each dependency sets exactly one
    /// reference shape, and any intra-template extension ref resolves to
    /// another path in this template.
    /// </summary>
    public static void ValidateExtensions(IReadOnlyList<ExtensionAuthoring> extensions, IDictionary<string, string> errors)
    {
        var pathsSeen = new HashSet<string>(StringComparer.Ordinal);
        var pathsCaseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < extensions.Count; i++)
        {
            var ext = extensions[i];
            var prefix = $"Extensions[{i}]";

            var path = ext.Path?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                errors[$"{prefix}.Path"] = "Extension path is required.";
            }
            else if (!ExtensionPathRegex.IsMatch(path))
            {
                errors[$"{prefix}.Path"] = "Extension path must start with a letter and contain only letters, digits, hyphens, or underscores.";
            }
            else if (!pathsSeen.Add(path))
            {
                errors[$"{prefix}.Path"] = $"Duplicate extension path '{path}'.";
            }
            else if (!pathsCaseInsensitive.Add(path))
            {
                errors[$"{prefix}.Path"] = $"Extension path '{path}' collides case-insensitively with another extension. Windows treats them as the same folder.";
            }

            if (string.IsNullOrWhiteSpace(ext.NameTemplate))
            {
                errors[$"{prefix}.NameTemplate"] = "Extension name template is required.";
            }

            // Both id-range bounds must be set together (or both omitted).
            // Half-set ranges would silently break the generator's auto-allocator.
            if (ext.IdRangeFrom is int from && ext.IdRangeTo is int to)
            {
                if (from <= 0) errors[$"{prefix}.IdRangeFrom"] = "Id range start must be greater than zero.";
                if (to <= from) errors[$"{prefix}.IdRangeTo"] = "Id range end must be greater than 'from'.";
            }
            else if (ext.IdRangeFrom is not null || ext.IdRangeTo is not null)
            {
                errors[$"{prefix}.IdRange"] = "Set both id_range_from and id_range_to, or neither.";
            }

            ValidateFolderTree(ext.Folders, prefix + ".Folders", errors);
            ValidateDependencies(ext.Dependencies, prefix + ".Dependencies", pathsSeen, errors);
        }
    }

    private static void ValidateFolderTree(IReadOnlyList<FolderAuthoring> folders, string prefix, IDictionary<string, string> errors)
    {
        var siblingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var folderPrefix = $"{prefix}[{i}]";
            var path = folder.Path?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(path))
            {
                errors[$"{folderPrefix}.Path"] = "Folder path is required.";
            }
            else if (!PathSegmentRegex.IsMatch(path) || path == "." || path == "..")
            {
                errors[$"{folderPrefix}.Path"] =
                    "Folder path must be a single segment — no slashes, no '..', no leading/trailing whitespace.";
            }
            else if (!siblingPaths.Add(path))
            {
                errors[$"{folderPrefix}.Path"] = $"Duplicate sibling folder '{path}' (case-insensitive).";
            }

            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var f = 0; f < folder.Files.Count; f++)
            {
                var file = folder.Files[f];
                var filePrefix = $"{folderPrefix}.Files[{f}]";
                var filePath = file.Path?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(filePath))
                {
                    errors[$"{filePrefix}.Path"] = "File path is required.";
                }
                else if (!PathSegmentRegex.IsMatch(filePath) || filePath == "." || filePath == "..")
                {
                    errors[$"{filePrefix}.Path"] =
                        "File path must be a basename — no slashes, no '..', no leading/trailing whitespace.";
                }
                else if (!fileNames.Add(filePath))
                {
                    errors[$"{filePrefix}.Path"] = $"Duplicate file '{filePath}' in this folder (case-insensitive).";
                }
            }

            ValidateFolderTree(folder.Folders, folderPrefix + ".Folders", errors);
        }
    }

    private static void ValidateDependencies(
        IReadOnlyList<DependencyAuthoring> deps,
        string prefix,
        IReadOnlySet<string> knownExtensionPaths,
        IDictionary<string, string> errors)
    {
        for (var i = 0; i < deps.Count; i++)
        {
            var dep = deps[i];
            var depPrefix = $"{prefix}[{i}]";

            // Exactly one of the three reference shapes must be set. The DB
            // CHECK constraint enforces this too, but field-keyed messages are
            // friendlier than a Postgres error string in the editor.
            var setCount = (dep.RefExtensionPath is not null ? 1 : 0)
                + (dep.RefModuleKey is not null ? 1 : 0)
                + (dep.LitId is not null ? 1 : 0);
            if (setCount == 0)
            {
                errors[depPrefix] = "Each dependency must set one of: extension, module, or id.";
                continue;
            }
            if (setCount > 1)
            {
                errors[depPrefix] = "A dependency must use only one of: extension, module, or id (not several).";
                continue;
            }

            if (dep.RefExtensionPath is string refPath
                && !knownExtensionPaths.Contains(refPath))
            {
                errors[$"{depPrefix}.Extension"] =
                    $"Dependency references extension '{refPath}', which isn't declared by this template.";
            }
            else if (dep.LitId is string litId)
            {
                // Lightweight GUID sanity check — the dep_id field is otherwise
                // free-form because AL accepts wrapped {GUID} and bare forms.
                if (litId.Length < 4)
                {
                    errors[$"{depPrefix}.Id"] = "Literal dependency id is too short.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitName))
                {
                    errors[$"{depPrefix}.Name"] = "Literal dependency name is required.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitPublisher))
                {
                    errors[$"{depPrefix}.Publisher"] = "Literal dependency publisher is required.";
                }
                if (string.IsNullOrWhiteSpace(dep.LitVersion))
                {
                    errors[$"{depPrefix}.Version"] = "Literal dependency version is required.";
                }
            }
        }
    }
}
