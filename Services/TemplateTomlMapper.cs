using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace ALDevToolbox.Services;

/// <summary>
/// Bridge between the persisted <see cref="RuntimeTemplate"/> shape and the
/// <c>template.toml</c> seed format. Lets the admin edit page render and parse
/// a template in its TOML representation as an alternative to the structured
/// form. The DB remains the source of truth — this class exists purely as an
/// editor serialisation, not a sync mechanism. The TOML schema is documented
/// in <c>.design/templates-and-seeding.md</c> and mirrors
/// <c>Templates.seed/runtime-*/template.toml</c>.
/// </summary>
public static class TemplateTomlMapper
{
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Serialises a template to its <c>template.toml</c> form. The two JSON
    /// columns are unpacked into proper <c>[defaults]</c> and <c>[appSourceCop]</c>
    /// tables so they're tractable to edit directly.
    /// </summary>
    public static string ToToml(RuntimeTemplate template)
    {
        var seed = new TemplateSeed
        {
            Template = new TemplateMetaSeed
            {
                Key = template.Key,
                Runtime = template.Runtime,
                Name = template.Name,
                Description = template.Description,
                DefaultApplication = template.DefaultApplication,
                DefaultPlatform = template.DefaultPlatform,
                CoreIdRangeFrom = template.CoreIdRangeFrom,
                CoreIdRangeTo = template.CoreIdRangeTo,
                ModuleIdRangeStart = template.ModuleIdRangeStart,
                ModuleIdRangeSize = template.ModuleIdRangeSize,
            },
            Defaults = new DefaultsSeed
            {
                Publisher = template.Defaults.Publisher,
                Target = template.Defaults.Target,
                Url = template.Defaults.Url,
                Logo = template.Defaults.Logo,
                Features = template.Defaults.Features.ToList(),
                SupportedLocales = template.Defaults.SupportedLocales.ToList(),
                ResourceExposurePolicy = new ResourceExposurePolicySeed
                {
                    AllowDebugging = template.Defaults.ResourceExposurePolicy.AllowDebugging,
                    AllowDownloadingSource = template.Defaults.ResourceExposurePolicy.AllowDownloadingSource,
                    IncludeSourceInSymbolFile = template.Defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
                },
            },
            AppSourceCop = new AppSourceCopSeed
            {
                MandatoryPrefix = template.AppSourceCop.MandatoryPrefix,
                SupportedCountries = template.AppSourceCop.SupportedCountries.ToList(),
            },
            Folders = template.Folders
                .OrderBy(f => f.Ordering)
                .Select(f => new FolderSeed
                {
                    Path = f.Path,
                    Files = f.Files
                        .OrderBy(x => x.Ordering)
                        .Select(x => new FolderFileSeed { Path = x.Path, Content = x.Content })
                        .ToList(),
                })
                .ToList(),
        };

        // Note that the Deprecated flag is intentionally not round-tripped
        // through TOML — seed TOML doesn't carry it, so we keep TOML editing
        // focused on the same surface as a fresh seed file. Toggle it via the
        // structured form instead.
        return TomlSerializer.Serialize(seed, TomlOptions);
    }

    /// <summary>
    /// Parses TOML back into a <see cref="TemplateInput"/> ready for
    /// <see cref="TemplateService.CreateAsync"/> or
    /// <see cref="TemplateService.UpdateAsync"/>. Throws
    /// <see cref="InvalidDataException"/> for malformed TOML so the caller can
    /// surface the underlying message inline.
    /// </summary>
    public static TemplateInput FromToml(string toml, bool deprecated)
    {
        // Strict mode: parse first so syntax errors surface with line numbers,
        // and walk the resulting model to flag unknown keys (e.g. `examplee`)
        // rather than letting the deserializer drop them silently.
        var doc = Toml.Parse(toml);
        if (doc.HasErrors)
        {
            var diagnostics = string.Join("\n", doc.Diagnostics.Select(d => $"  - {d}"));
            throw new InvalidDataException($"Failed to parse TOML:\n{diagnostics}");
        }

        var unknown = FindUnknownKeys(doc.ToModel());
        if (unknown.Count > 0)
        {
            throw new InvalidDataException(
                "Unknown TOML keys (typo or not supported by this template format):\n"
                + string.Join("\n", unknown.Select(k => $"  - {k}")));
        }

        TemplateSeed seed;
        try
        {
            seed = TomlSerializer.Deserialize<TemplateSeed>(toml, TomlOptions) ?? new TemplateSeed();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse TOML: {ex.Message}", ex);
        }

        var defaults = new Domain.ValueObjects.TemplateDefaults
        {
            Publisher = seed.Defaults.Publisher,
            Target = seed.Defaults.Target,
            Url = seed.Defaults.Url,
            Logo = seed.Defaults.Logo,
            Features = seed.Defaults.Features.ToList(),
            SupportedLocales = seed.Defaults.SupportedLocales.ToList(),
            ResourceExposurePolicy = new Domain.ValueObjects.ResourceExposurePolicy
            {
                AllowDebugging = seed.Defaults.ResourceExposurePolicy.AllowDebugging,
                AllowDownloadingSource = seed.Defaults.ResourceExposurePolicy.AllowDownloadingSource,
                IncludeSourceInSymbolFile = seed.Defaults.ResourceExposurePolicy.IncludeSourceInSymbolFile,
            },
        };

        var appSourceCop = new Domain.ValueObjects.AppSourceCopSettings
        {
            MandatoryPrefix = seed.AppSourceCop.MandatoryPrefix,
            SupportedCountries = seed.AppSourceCop.SupportedCountries.ToList(),
        };

        // Round-trip the JSON columns through the same converter the structured
        // form would use, so we hit identical validation and parsing paths.
        var defaultsJson = JsonSerializer.Serialize(defaults);
        var appSourceCopJson = JsonSerializer.Serialize(appSourceCop);

        return new TemplateInput(
            Key: seed.Template.Key,
            Runtime: seed.Template.Runtime,
            Name: seed.Template.Name,
            Description: seed.Template.Description,
            DefaultApplication: seed.Template.DefaultApplication,
            DefaultPlatform: seed.Template.DefaultPlatform,
            DefaultsJson: defaultsJson,
            AppSourceCopJson: appSourceCopJson,
            CoreIdRangeFrom: seed.Template.CoreIdRangeFrom,
            CoreIdRangeTo: seed.Template.CoreIdRangeTo,
            ModuleIdRangeStart: seed.Template.ModuleIdRangeStart,
            ModuleIdRangeSize: seed.Template.ModuleIdRangeSize,
            Deprecated: deprecated,
            Folders: seed.Folders.Select(f => new TemplateFolderInput(
                f.Path,
                f.Files.Select(x => new TemplateFileInput(x.Path, x.Content)).ToList())).ToList());
    }

    /// <summary>
    /// Walks the parsed model and reports any keys whose snake_case name
    /// doesn't map to a property on the corresponding seed type. Used for
    /// strict TOML validation in the admin editor.
    /// </summary>
    private static List<string> FindUnknownKeys(TomlTable root)
    {
        var unknown = new List<string>();
        WalkTable(root, typeof(TemplateSeed), prefix: string.Empty, unknown);
        return unknown;

        static void WalkTable(TomlTable table, Type modelType, string prefix, List<string> sink)
        {
            // Map TOML keys back onto the model's properties. Most properties
            // follow the global SnakeCaseLower policy, but the seed types
            // override individual outliers with [TomlPropertyName(...)] —
            // honour those first so e.g. <c>appSourceCop</c> still resolves.
            var properties = new Dictionary<string, System.Reflection.PropertyInfo>(StringComparer.Ordinal);
            foreach (var p in modelType.GetProperties())
            {
                var tomlName = (Attribute.GetCustomAttribute(p, typeof(TomlPropertyNameAttribute))
                                as TomlPropertyNameAttribute)?.Name ?? ToSnakeCase(p.Name);
                properties[tomlName] = p;
            }

            foreach (var entry in table)
            {
                if (!properties.TryGetValue(entry.Key, out var prop))
                {
                    sink.Add(string.IsNullOrEmpty(prefix) ? entry.Key : $"{prefix}.{entry.Key}");
                    continue;
                }
                var path = string.IsNullOrEmpty(prefix) ? entry.Key : $"{prefix}.{entry.Key}";
                if (entry.Value is TomlTable child)
                {
                    WalkTable(child, prop.PropertyType, path, sink);
                }
                else if (entry.Value is TomlTableArray array)
                {
                    var elementType = prop.PropertyType.IsGenericType
                        ? prop.PropertyType.GetGenericArguments().FirstOrDefault()
                        : null;
                    if (elementType is null) continue;
                    for (var i = 0; i < array.Count; i++)
                    {
                        WalkTable(array[i], elementType, $"{path}[{i}]", sink);
                    }
                }
            }
        }

        static string ToSnakeCase(string pascal)
        {
            var sb = new System.Text.StringBuilder(pascal.Length + 4);
            for (var i = 0; i < pascal.Length; i++)
            {
                var c = pascal[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Builds a starter TOML document for the New Template flow. Mirrors the
    /// structure of a fresh <c>template.toml</c> but with placeholder values so
    /// the admin can edit and save in one step.
    /// </summary>
    public static string BlankToml()
    {
        var seed = new TemplateSeed
        {
            Template = new TemplateMetaSeed
            {
                Key = "runtime-new",
                Runtime = 0,
                Name = string.Empty,
                Description = null,
                DefaultApplication = string.Empty,
                DefaultPlatform = "1.0.0.0",
                CoreIdRangeFrom = 90000,
                CoreIdRangeTo = 90999,
                ModuleIdRangeStart = 91000,
                ModuleIdRangeSize = 200,
            },
        };
        return TomlSerializer.Serialize(seed, TomlOptions);
    }
}
