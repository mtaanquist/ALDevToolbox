using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using Tomlyn;

namespace ALDevToolbox.Services;

/// <summary>
/// Bridge between the persisted <see cref="RuntimeTemplate"/> shape and the
/// <c>template.toml</c> document format under the unified-extensions model.
/// The DB remains the source of truth — this class is editor-only
/// serialisation. The TOML schema lives in
/// <c>.design/unified-extensions.md</c> and <c>.design/templates-and-seeding.md</c>.
/// </summary>
public static class TemplateTomlMapper
{
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    /// <summary>
    /// Folder paths a fresh template starts with. Kept in sync with the
    /// blank-template starter; mirrors the static fallback folders the
    /// generator would otherwise emit so the preview pane is non-empty.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFolderPaths = new[]
    {
        "libs",
        "permissionsets",
        "Translations",
    };

    // ===== ToToml =====

    /// <summary>
    /// Serialises a template to its <c>template.toml</c> form. The two JSON
    /// columns are unpacked into <c>[defaults]</c> and <c>[appSourceCop]</c>
    /// tables. The recursive folder tree under each extension is emitted by
    /// hand (Tomlyn's reflection serialiser produces inline arrays that are
    /// unreadable for non-trivial templates).
    /// </summary>
    public static string ToToml(RuntimeTemplate template)
    {
        var seed = BuildHeaderSeed(template);

        // Serialise just the metadata + defaults sections via Tomlyn, then
        // strip its empty extensions array and append the [[extensions]]
        // blocks by hand.
        var head = TomlSerializer.Serialize(seed, TomlOptions);
        head = EmptyExtensionsLineRegex.Replace(head, string.Empty).TrimEnd();

        var sb = new StringBuilder(head);
        foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
        {
            AppendExtension(sb, ext);
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static TemplateSeed BuildHeaderSeed(RuntimeTemplate template)
    {
        var defaults = template.Defaults ?? new TemplateDefaults();
        var appSourceCop = template.AppSourceCop ?? new AppSourceCopSettings();

        return new TemplateSeed
        {
            Template = new TemplateMetaSeed
            {
                Key = template.Key,
                Runtime = template.Runtime,
                Name = template.Name,
                Description = template.Description,
                CoreIdRangeFrom = template.CoreIdRangeFrom,
                CoreIdRangeTo = template.CoreIdRangeTo,
                ModuleIdRangeStart = template.ModuleIdRangeStart,
                ModuleIdRangeSize = template.ModuleIdRangeSize,
                IsDefault = template.IsDefault,
                DefaultApplicationVersion = template.DefaultApplicationVersion?.Key,
                DefaultModules = template.DefaultModules
                    .OrderBy(d => d.Ordering)
                    .Where(d => d.Module is not null)
                    .Select(d => new TemplateDefaultModuleSeed { Key = d.Module!.Key })
                    .ToList(),
            },
            Defaults = new DefaultsSeed
            {
                Publisher = defaults.Publisher,
                Target = defaults.Target,
                Application = defaults.Application,
                Platform = defaults.Platform,
                ExtensionPrefix = defaults.ExtensionPrefix,
                Url = defaults.Url,
                Logo = defaults.Logo,
                Features = defaults.Features.ToList(),
                SupportedLocales = defaults.SupportedLocales.ToList(),
                Affix = defaults.Affix,
                AffixType = defaults.AffixType.ToString(),
                ResourceExposurePolicy = new ResourceExposurePolicySeed
                {
                    AllowDebugging = defaults.ResourceExposurePolicy.AllowDebugging,
                    AllowDownloadingSource = defaults.ResourceExposurePolicy.AllowDownloadingSource,
                    IncludeSourceInSymbolFile = defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
                },
            },
            AppSourceCop = new AppSourceCopSeed
            {
                MandatoryPrefix = appSourceCop.MandatoryPrefix,
                SupportedCountries = appSourceCop.SupportedCountries.ToList(),
            },
            // Extensions filled in manually after Tomlyn serialisation.
        };
    }

    private static void AppendExtension(StringBuilder sb, WorkspaceExtension ext)
    {
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[[extensions]]");
        sb.Append("path = ").AppendLine(TomlBasicString(ext.Path));
        sb.Append("name = ").AppendLine(TomlBasicString(ext.NameTemplate));
        if (!ext.Required)
        {
            sb.AppendLine("required = false");
        }
        if (!string.IsNullOrEmpty(ext.Application))
        {
            sb.Append("application = ").AppendLine(TomlBasicString(ext.Application));
        }
        if (!string.IsNullOrEmpty(ext.Runtime))
        {
            sb.Append("runtime = ").AppendLine(TomlBasicString(ext.Runtime));
        }
        if (ext.IdRangeFrom is int from)
        {
            sb.Append("id_range_from = ").AppendLine(from.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (ext.IdRangeTo is int to)
        {
            sb.Append("id_range_to = ").AppendLine(to.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var dep in ext.Dependencies.OrderBy(d => d.Ordering))
        {
            AppendDependency(sb, dep);
        }

        foreach (var folder in ext.Folders.OrderBy(f => f.Ordering))
        {
            AppendFolder(sb, folder, parentPath: "extensions");
        }
    }

    private static void AppendDependency(StringBuilder sb, WorkspaceExtensionDependency dep)
    {
        sb.AppendLine();
        sb.AppendLine("[[extensions.dependencies]]");
        if (dep.RefExtensionPath is not null)
        {
            sb.Append("extension = ").AppendLine(TomlBasicString(dep.RefExtensionPath));
        }
        else if (dep.RefModuleKey is not null)
        {
            sb.Append("module = ").AppendLine(TomlBasicString(dep.RefModuleKey));
        }
        else
        {
            sb.Append("id = ").AppendLine(TomlBasicString(dep.LitId ?? string.Empty));
            sb.Append("name = ").AppendLine(TomlBasicString(dep.LitName ?? string.Empty));
            sb.Append("publisher = ").AppendLine(TomlBasicString(dep.LitPublisher ?? string.Empty));
            sb.Append("version = ").AppendLine(TomlBasicString(dep.LitVersion ?? string.Empty));
        }
    }

    /// <summary>
    /// Recursively emit a folder as <c>[[parentPath.folders]]</c>, its files
    /// as <c>[[parentPath.folders.files]]</c>, and child folders as
    /// <c>[[parentPath.folders.folders]]</c>. <paramref name="parentPath"/>
    /// is the TOML key prefix accumulating as we descend.
    /// </summary>
    private static void AppendFolder(StringBuilder sb, WorkspaceExtensionFolder folder, string parentPath)
    {
        var folderKey = $"{parentPath}.folders";
        sb.AppendLine();
        sb.Append("[[").Append(folderKey).AppendLine("]]");
        sb.Append("path = ").AppendLine(TomlBasicString(folder.Path));

        foreach (var file in folder.Files.OrderBy(f => f.Ordering))
        {
            sb.AppendLine();
            sb.Append("[[").Append(folderKey).AppendLine(".files]]");
            sb.Append("path = ").AppendLine(TomlBasicString(file.Path));
            sb.Append("content = ").AppendLine(TomlMultilineBasic(file.Content));
            if (file.IsExample)
            {
                sb.AppendLine("is_example = true");
            }
        }

        foreach (var child in folder.Folders.OrderBy(f => f.Ordering))
        {
            AppendFolder(sb, child, folderKey);
        }
    }

    // ===== FromToml =====

    /// <summary>
    /// Parses TOML into a <see cref="TemplateAuthoring"/> ready for downstream
    /// reconciliation. Throws <see cref="TomlParseException"/> with
    /// line-aware diagnostics on syntax errors.
    /// </summary>
    public static TemplateAuthoring FromToml(string toml, bool deprecated)
    {
        TemplateSeed seed;
        try
        {
            seed = TomlSerializer.Deserialize<TemplateSeed>(NormalizeRuntimeValue(toml), TomlOptions) ?? new TemplateSeed();
        }
        catch (TomlException tex)
        {
            var issues = tex.Diagnostics
                .Select(d => new TomlParseIssue(
                    Line: d.Span.Start.Line + 1,
                    Column: d.Span.Start.Column + 1,
                    Message: d.Message))
                .ToList();
            var summary = issues.Count == 0
                ? tex.Message
                : string.Join("; ", issues.Take(3).Select(i => $"line {i.Line}: {i.Message}"));
            throw new TomlParseException($"Failed to parse TOML: {summary}", issues, tex);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse TOML: {ex.Message}", ex);
        }

        // Materialise the strongly-typed value objects so we can serialise
        // them back through the same converter the structured form path uses.
        // affixType comes in as a string; tolerate case / whitespace and fall
        // back to None on anything unrecognised.
        var defaults = new TemplateDefaults
        {
            Publisher = seed.Defaults.Publisher,
            Target = seed.Defaults.Target,
            Application = seed.Defaults.Application,
            Platform = seed.Defaults.Platform,
            ExtensionPrefix = seed.Defaults.ExtensionPrefix,
            Url = seed.Defaults.Url,
            Logo = seed.Defaults.Logo,
            Features = seed.Defaults.Features.ToList(),
            SupportedLocales = seed.Defaults.SupportedLocales.ToList(),
            Affix = seed.Defaults.Affix,
            AffixType = ParseAffixType(seed.Defaults.AffixType),
            ResourceExposurePolicy = new ResourceExposurePolicy
            {
                AllowDebugging = seed.Defaults.ResourceExposurePolicy.AllowDebugging,
                AllowDownloadingSource = seed.Defaults.ResourceExposurePolicy.AllowDownloadingSource,
                IncludeSourceInSymbolFile = seed.Defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
            },
        };

        var appSourceCop = new AppSourceCopSettings
        {
            MandatoryPrefix = seed.AppSourceCop.MandatoryPrefix,
            SupportedCountries = seed.AppSourceCop.SupportedCountries.ToList(),
        };

        var extensions = seed.Extensions.Select(MapExtension).ToList();

        return new TemplateAuthoring(
            Key: seed.Template.Key,
            Runtime: seed.Template.Runtime,
            Name: seed.Template.Name,
            Description: seed.Template.Description,
            DefaultsJson: JsonSerializer.Serialize(defaults, JsonOptions),
            AppSourceCopJson: JsonSerializer.Serialize(appSourceCop, JsonOptions),
            CoreIdRangeFrom: seed.Template.CoreIdRangeFrom,
            CoreIdRangeTo: seed.Template.CoreIdRangeTo,
            ModuleIdRangeStart: seed.Template.ModuleIdRangeStart,
            ModuleIdRangeSize: seed.Template.ModuleIdRangeSize,
            Deprecated: deprecated,
            IsDefault: seed.Template.IsDefault,
            DefaultApplicationVersionKey: string.IsNullOrWhiteSpace(seed.Template.DefaultApplicationVersion)
                ? null
                : seed.Template.DefaultApplicationVersion,
            DefaultModuleKeys: seed.Template.DefaultModules.Select(d => d.Key).ToList(),
            Extensions: extensions);
    }

    private static ExtensionAuthoring MapExtension(ExtensionSeed seed) => new(
        Path: seed.Path,
        NameTemplate: seed.Name,
        Required: seed.Required,
        Application: string.IsNullOrEmpty(seed.Application) ? null : seed.Application,
        Runtime: string.IsNullOrEmpty(seed.Runtime) ? null : seed.Runtime,
        IdRangeFrom: seed.IdRangeFrom,
        IdRangeTo: seed.IdRangeTo,
        Folders: seed.Folders.Select(MapFolder).ToList(),
        Dependencies: seed.Dependencies.Select(MapDependency).ToList());

    private static FolderAuthoring MapFolder(FolderSeed seed) => new(
        Path: seed.Path,
        Folders: seed.Folders.Select(MapFolder).ToList(),
        Files: seed.Files.Select(f => new FileAuthoring(f.Path, f.Content, f.IsExample)).ToList());

    private static DependencyAuthoring MapDependency(DependencySeed seed)
    {
        // Tomlyn fills in all string properties even when absent (default "")
        // — collapse empty strings to null so the one-of contract holds.
        var ext = string.IsNullOrEmpty(seed.Extension) ? null : seed.Extension;
        var mod = string.IsNullOrEmpty(seed.Module) ? null : seed.Module;
        var litId = string.IsNullOrEmpty(seed.Id) ? null : seed.Id;
        return new DependencyAuthoring(
            RefExtensionPath: ext,
            RefModuleKey: mod,
            LitId: litId,
            LitName: string.IsNullOrEmpty(seed.Name) ? null : seed.Name,
            LitPublisher: string.IsNullOrEmpty(seed.Publisher) ? null : seed.Publisher,
            LitVersion: string.IsNullOrEmpty(seed.Version) ? null : seed.Version);
    }

    private static AffixType ParseAffixType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AffixType.None;
        return raw.Trim() switch
        {
            "Prefix" or "prefix" or "PREFIX" => AffixType.Prefix,
            "Suffix" or "suffix" or "SUFFIX" => AffixType.Suffix,
            _ => AffixType.None,
        };
    }

    // ===== Blank starter =====

    /// <summary>
    /// Starter TOML document for the New Template flow. Mirrors the structure
    /// of a fresh customer-style template: a required <c>Core</c> extension
    /// pre-declaring the standard AL fallback folders, no files, no
    /// dependencies. Admins fill in the rest.
    /// </summary>
    public static string BlankToml()
    {
        var seed = new TemplateSeed
        {
            Template = new TemplateMetaSeed
            {
                Key = "runtime-new",
                Runtime = "0",
                Name = string.Empty,
                Description = null,
                CoreIdRangeFrom = 90000,
                CoreIdRangeTo = 90999,
                ModuleIdRangeStart = 91000,
                ModuleIdRangeSize = 200,
            },
            Defaults = new DefaultsSeed
            {
                Platform = "1.0.0.0",
            },
        };

        var head = TomlSerializer.Serialize(seed, TomlOptions);
        head = EmptyExtensionsLineRegex.Replace(head, string.Empty).TrimEnd();

        var sb = new StringBuilder(head);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[[extensions]]");
        sb.AppendLine("path = \"Core\"");
        sb.AppendLine("name = \"{{extension_prefix}} Core\"");
        sb.AppendLine("required = true");
        foreach (var path in DefaultFolderPaths)
        {
            sb.AppendLine();
            sb.AppendLine("[[extensions.folders]]");
            sb.Append("path = ").AppendLine(TomlBasicString(path));
        }
        sb.AppendLine();
        return sb.ToString();
    }

    // ===== Runtime normalisation =====

    private static readonly Regex UnquotedRuntimeRegex =
        new(@"(?m)^(\s*runtime\s*=\s*)(\d+(?:\.\d+)?)\s*(#.*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Wraps bare <c>runtime = 15</c> values in quotes so Tomlyn can
    /// deserialise them into the seed's string runtime field. Carried over
    /// from the pre-unified mapper.
    /// </summary>
    public static string NormalizeRuntimeValue(string toml)
    {
        if (string.IsNullOrEmpty(toml)) return toml;
        return UnquotedRuntimeRegex.Replace(toml, m =>
        {
            var prefix = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            var trailing = m.Groups[3].Success ? " " + m.Groups[3].Value : string.Empty;
            return $"{prefix}\"{value}\"{trailing}";
        });
    }

    // ===== TOML string helpers =====

    /// <summary>
    /// Empty-arrays line that Tomlyn emits for the (intentionally empty)
    /// <c>extensions</c> field on the header seed. Stripped before we append
    /// the hand-emitted <c>[[extensions]]</c> blocks.
    /// </summary>
    private static readonly Regex EmptyExtensionsLineRegex =
        new(@"(?m)^extensions\s*=\s*\[\s*\]\s*\r?\n?", RegexOptions.Compiled);

    private static string TomlBasicString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string TomlMultilineBasic(string content)
    {
        var escaped = content
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
        return $"\"\"\"\n{escaped}\"\"\"";
    }
}
