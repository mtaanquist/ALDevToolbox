using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Tests.Builders;

/// <summary>
/// Constructs <see cref="RuntimeTemplate"/> rows pre-populated with sensible
/// defaults so tests only spell out the fields they care about. Resist the
/// temptation to grow this beyond fluent setters and a couple of canned shapes.
/// </summary>
public static class TemplateBuilder
{
    public static RuntimeTemplate Default(string key = "runtime-test", string runtime = "15") => new()
    {
        Key = key,
        Runtime = runtime,
        Name = "Test Runtime",
        Description = "Synthetic template used in tests.",
        DefaultApplication = "24.0.0.0",
        DefaultPlatform = "1.0.0.0",
        Defaults = new TemplateDefaults
        {
            Publisher = "Acme",
            Target = "Cloud",
            Features = new List<string> { "TranslationFile" },
            SupportedLocales = new List<string> { "en-US" },
        },
        AppSourceCop = new AppSourceCopSettings
        {
            MandatoryPrefix = "ACME",
            SupportedCountries = new List<string> { "US" },
        },
        CoreIdRangeFrom = 90000,
        CoreIdRangeTo = 90999,
        ModuleIdRangeStart = 91000,
        ModuleIdRangeSize = 200,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static RuntimeTemplate WithCoreFolder(this RuntimeTemplate template, string path, params (string Path, string Content)[] files)
    {
        var folder = new TemplateFolder
        {
            Ordering = template.Folders.Count,
            Path = path,
        };
        for (var i = 0; i < files.Length; i++)
        {
            folder.Files.Add(new TemplateFile
            {
                Ordering = i,
                Path = files[i].Path,
                Content = files[i].Content,
            });
        }
        template.Folders.Add(folder);
        return template;
    }

    public static RuntimeTemplate WithModuleFolder(this RuntimeTemplate template, string path, params (string Path, string Content)[] files)
    {
        var folder = new TemplateModuleFolder
        {
            Ordering = template.ModuleFolders.Count,
            Path = path,
        };
        for (var i = 0; i < files.Length; i++)
        {
            folder.Files.Add(new TemplateModuleFile
            {
                Ordering = i,
                Path = files[i].Path,
                Content = files[i].Content,
            });
        }
        template.ModuleFolders.Add(folder);
        return template;
    }
}
