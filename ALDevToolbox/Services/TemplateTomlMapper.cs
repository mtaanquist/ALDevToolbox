using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using Tomlyn;

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
                DefaultModules = template.DefaultModules
                    .OrderBy(d => d.Ordering)
                    .Where(d => d.Module is not null)
                    .Select(d => d.Module!.Key)
                    .ToList(),
                DefaultApplicationVersion = template.DefaultApplicationVersion?.Key,
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
            // Folders intentionally left empty for the high-level serializer —
            // it would emit them as a single inline `folders = [{...}, {...}]`
            // line which is unreadable for non-trivial templates. We strip the
            // empty `folders = []` line below and append [[folders]] /
            // [[folders.files]] blocks ourselves so each entry sits on its own
            // line. Tomlyn deserialises both forms back into List<FolderSeed>,
            // so FromToml keeps working unchanged.
        };

        // Note that the Deprecated flag is intentionally not round-tripped
        // through TOML — seed TOML doesn't carry it, so we keep TOML editing
        // focused on the same surface as a fresh seed file. Toggle it via the
        // structured form instead.
        var head = TomlSerializer.Serialize(seed, TomlOptions);
        head = EmptyFoldersLineRegex.Replace(head, string.Empty).TrimEnd();

        var sb = new StringBuilder(head);
        foreach (var folder in template.Folders.OrderBy(f => f.Ordering))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("[[folders]]");
            sb.Append("path = ").AppendLine(TomlBasicString(folder.Path));
            foreach (var file in folder.Files.OrderBy(x => x.Ordering))
            {
                sb.AppendLine();
                sb.AppendLine("[[folders.files]]");
                sb.Append("path = ").AppendLine(TomlBasicString(file.Path));
                sb.Append("content = ").AppendLine(TomlMultilineBasic(file.Content));
            }
        }
        foreach (var folder in template.ModuleFolders.OrderBy(f => f.Ordering))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("[[module_folders]]");
            sb.Append("path = ").AppendLine(TomlBasicString(folder.Path));
            foreach (var file in folder.Files.OrderBy(x => x.Ordering))
            {
                sb.AppendLine();
                sb.AppendLine("[[module_folders.files]]");
                sb.Append("path = ").AppendLine(TomlBasicString(file.Path));
                sb.Append("content = ").AppendLine(TomlMultilineBasic(file.Content));
            }
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static readonly Regex EmptyFoldersLineRegex =
        new(@"(?m)^(folders|module_folders)\s*=\s*\[\s*\]\s*\r?\n?", RegexOptions.Compiled);

    /// <summary>TOML basic-string encoding for short, single-line values like paths.</summary>
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

    /// <summary>
    /// TOML multi-line basic string (<c>"""…"""</c>) for arbitrary file
    /// content. Matches the format the seed TOML files use, so round-trips
    /// through the editor produce documents that look like the originals.
    /// Backslashes are doubled and any literal <c>"""</c> in the content is
    /// escaped per character so the closing delimiter stays unambiguous; every
    /// other character (newlines, tabs, single/double quotes) passes through
    /// verbatim because multi-line basic strings allow them. The newline
    /// immediately after the opening delimiter is trimmed by the TOML parser,
    /// so the round trip is exact.
    /// </summary>
    private static string TomlMultilineBasic(string content)
    {
        var escaped = content
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
        return $"\"\"\"\n{escaped}\"\"\"";
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
        // Tomlyn 2.3.2 doesn't expose a stable strict-parse hook — the
        // System.Text.Json-style TomlSerializer surface deliberately drops
        // unknown keys. We surface syntax errors via the deserializer's
        // exception path; the bulleted-error display in the admin editor
        // makes the resulting field-keyed messages tractable to scan.
        TemplateSeed seed;
        try
        {
            seed = TomlSerializer.Deserialize<TemplateSeed>(NormalizeRuntimeValue(toml), TomlOptions) ?? new TemplateSeed();
        }
        catch (Tomlyn.TomlException tex)
        {
            // Tomlyn diagnostics carry zero-based line/column positions.
            // Promote them to 1-based so they line up with the gutter the
            // CodeMirror editor renders on the admin TOML tab.
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
            DefaultModuleKeys: seed.Template.DefaultModules.ToList(),
            DefaultApplicationVersionKey: string.IsNullOrWhiteSpace(seed.Template.DefaultApplicationVersion)
                ? null
                : seed.Template.DefaultApplicationVersion,
            Folders: seed.Folders.Select(f => new TemplateFolderInput(
                f.Path,
                f.Files.Select(x => new TemplateFileInput(x.Path, x.Content)).ToList())).ToList(),
            ModuleFolders: seed.ModuleFolders.Select(f => new TemplateFolderInput(
                f.Path,
                f.Files.Select(x => new TemplateFileInput(x.Path, x.Content)).ToList())).ToList());
    }

    private static readonly Regex UnquotedRuntimeRegex =
        new(@"(?m)^(\s*runtime\s*=\s*)(\d+(?:\.\d+)?)\s*(#.*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Wraps unquoted <c>runtime = 15</c> / <c>runtime = 15.2</c> values in
    /// quotes so Tomlyn can deserialise them into the seed's
    /// <see cref="TemplateMetaSeed.Runtime"/> string property. Already-quoted
    /// values pass through untouched. Lets old seed files (and pasted snippets
    /// from the same era) keep parsing while the schema moves on. Doesn't
    /// fully scope to the <c>[template]</c> section — but the regex anchors on
    /// the literal key <c>runtime</c> at line start, which doesn't collide
    /// with the rest of the template schema.
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
                Runtime = "0",
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
