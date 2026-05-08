using System.Text.Json;
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
