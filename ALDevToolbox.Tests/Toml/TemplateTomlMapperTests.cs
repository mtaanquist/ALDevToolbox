using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using FluentAssertions;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Round-trip tests for <see cref="TemplateTomlMapper"/>: a TOML document goes
/// through ToToml → FromToml → ToToml and the second serialisation must match
/// the first. Covers the seed schema's tricky bits — folders + files, default
/// modules, application-version key, the unquoted-runtime backwards
/// compatibility shim, and the multi-line-basic content escaping.
/// </summary>
public class TemplateTomlMapperTests
{
    [Fact]
    public void Round_trip_preserves_metadata_defaults_and_appsourcecop()
    {
        var template = TemplateBuilder.Default("runtime-15", "15.2");
        template.Defaults.Url = "https://example.com/";
        template.Defaults.Logo = "../.assets/images/logo.png";
        template.Defaults.Features = new List<string> { "TranslationFile", "NoImplicitWith" };
        template.Defaults.SupportedLocales = new List<string> { "en-US", "da-DK" };
        template.AppSourceCop.SupportedCountries = new List<string> { "US", "DK" };

        var toml = TemplateTomlMapper.ToToml(template);
        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.Key.Should().Be("runtime-15");
        input.Runtime.Should().Be("15.2");
        input.Name.Should().Be(template.Name);
        input.DefaultApplication.Should().Be("24.0.0.0");
        input.CoreIdRangeFrom.Should().Be(90000);
        input.CoreIdRangeTo.Should().Be(90999);
        input.ModuleIdRangeStart.Should().Be(91000);
        input.ModuleIdRangeSize.Should().Be(200);
        input.DefaultsJson.Should().Contain("\"publisher\":\"Acme\"");
        input.DefaultsJson.Should().Contain("\"url\":\"https://example.com/\"");
        input.AppSourceCopJson.Should().Contain("\"mandatoryPrefix\":\"ACME\"");
        input.AppSourceCopJson.Should().Contain("DK");
    }

    [Fact]
    public void Round_trip_preserves_folders_with_files_in_order()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source/Foundation",
                ("AppInstall.al", "codeunit 90100 Sample {}\n"),
                ("AppUpgrade.al", "codeunit 90101 Upgrade {}\n"))
            .WithCoreFolder("Permissions");

        var toml = TemplateTomlMapper.ToToml(template);
        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.Folders.Should().HaveCount(2);
        input.Folders[0].Path.Should().Be("Source/Foundation");
        input.Folders[0].Files.Should().HaveCount(2);
        input.Folders[0].Files[0].Path.Should().Be("AppInstall.al");
        input.Folders[0].Files[0].Content.Should().Be("codeunit 90100 Sample {}\n");
        input.Folders[0].Files[1].Path.Should().Be("AppUpgrade.al");
        input.Folders[1].Path.Should().Be("Permissions");
        input.Folders[1].Files.Should().BeEmpty();
    }

    [Fact]
    public void Round_trip_preserves_module_folders()
    {
        var template = TemplateBuilder.Default()
            .WithModuleFolder("Source", ("Module.al", "codeunit 91100 Mod {}\n"));

        var toml = TemplateTomlMapper.ToToml(template);
        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.ModuleFolders.Should().HaveCount(1);
        input.ModuleFolders[0].Path.Should().Be("Source");
        input.ModuleFolders[0].Files[0].Content.Should().Be("codeunit 91100 Mod {}\n");
    }

    [Fact]
    public void Round_trip_preserves_default_modules_and_application_version_key()
    {
        var template = TemplateBuilder.Default();
        // The mapper reads Module.Key off each row, so attach minimally-shaped
        // Module instances rather than going through the DB.
        template.DefaultModules = new List<RuntimeTemplateDefaultModule>
        {
            new() { Ordering = 0, Module = ModuleBuilder.Default("alpha", "Alpha") },
            new() { Ordering = 1, Module = ModuleBuilder.Default("beta", "Beta") },
        };
        template.DefaultApplicationVersion = new ApplicationVersion
        {
            Key = "bc-v24",
            Name = "BC v24",
            Application = "24.0.0.0",
            Runtime = "15",
        };

        var toml = TemplateTomlMapper.ToToml(template);
        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.DefaultModuleKeys.Should().Equal("alpha", "beta");
        input.DefaultApplicationVersionKey.Should().Be("bc-v24");
    }

    [Fact]
    public void Round_trip_preserves_file_content_with_triple_quotes()
    {
        // The multi-line basic string used for file content can't include a
        // literal """ — the mapper escapes those per character. Round-trip
        // a deliberately gnarly file to make sure the escaping is reversible.
        const string content = "before \"\"\" after\nline two\twith tab\nand a backslash \\ and quote \"\n";
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source", ("Tricky.al", content));

        var toml = TemplateTomlMapper.ToToml(template);
        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.Folders[0].Files[0].Content.Should().Be(content);
    }

    [Fact]
    public void Bare_runtime_value_in_seed_toml_is_parsed_as_string()
    {
        // Older seed files used `runtime = 15` (no quotes); the schema is now
        // a string. NormalizeRuntimeValue wraps the bare value so Tomlyn can
        // still deserialise it.
        const string toml = """
            [template]
            key = "runtime-old"
            runtime = 15
            name = "Older runtime"
            default_application = "24.0.0.0"
            default_platform = "1.0.0.0"
            core_id_range_from = 90000
            core_id_range_to = 90999
            module_id_range_start = 91000
            module_id_range_size = 200
            """;

        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.Runtime.Should().Be("15");
    }

    [Fact]
    public void Blank_toml_round_trips_with_default_folders_in_both_lists()
    {
        // The New Template flow seeds the editor with BlankToml(). The starter
        // must parse cleanly and carry the libs/permissionsets/Translations
        // folders in both [[folders]] and [[module_folders]] so the preview pane
        // shows the standard AL layout without the page synthesising fallback
        // folders.
        var toml = TemplateTomlMapper.BlankToml();

        var input = TemplateTomlMapper.FromToml(toml, deprecated: false);

        input.Key.Should().Be("runtime-new");
        input.Folders.Select(f => f.Path).Should().Equal(TemplateTomlMapper.DefaultFolderPaths);
        input.ModuleFolders.Select(f => f.Path).Should().Equal(TemplateTomlMapper.DefaultFolderPaths);
        input.Folders.Should().OnlyContain(f => f.Files.Count == 0);
        input.ModuleFolders.Should().OnlyContain(f => f.Files.Count == 0);
    }

    [Fact]
    public void Malformed_toml_throws_with_line_columns()
    {
        const string toml = "this is = not = valid = toml";

        var act = () => TemplateTomlMapper.FromToml(toml, deprecated: false);

        var ex = act.Should().Throw<TomlParseException>().Which;
        ex.Issues.Should().NotBeEmpty();
        ex.Issues[0].Line.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public void Second_round_trip_is_byte_identical_to_first()
    {
        // The strongest round-trip guarantee — toml1 → input → toml2 must equal
        // toml2 → input → toml3. Catches subtle drift like reordering keys or
        // dropping optional fields between serialisations.
        var template = TemplateBuilder.Default()
            .WithCoreFolder("Source", ("Sample.al", "// hello {{name}}\n"))
            .WithModuleFolder("Source", ("Mod.al", "// module {{moduleName}}\n"));
        template.DefaultModules = new List<RuntimeTemplateDefaultModule>
        {
            new() { Ordering = 0, Module = ModuleBuilder.Default("alpha", "Alpha") },
        };

        var toml1 = TemplateTomlMapper.ToToml(template);

        var input = TemplateTomlMapper.FromToml(toml1, deprecated: false);
        var rebuilt = RebuildFromInput(input);
        var toml2 = TemplateTomlMapper.ToToml(rebuilt);

        toml2.Should().Be(toml1);
    }

    /// <summary>
    /// Minimal mapping from <see cref="TemplateInput"/> back to a
    /// <see cref="RuntimeTemplate"/> shape that <see cref="TemplateTomlMapper.ToToml"/>
    /// will accept. This is what <see cref="TemplateService.CreateAsync"/> does
    /// internally, but staying out of EF makes the round-trip purely
    /// deterministic.
    /// </summary>
    private static RuntimeTemplate RebuildFromInput(TemplateInput input)
    {
        var template = new RuntimeTemplate
        {
            Key = input.Key,
            Runtime = input.Runtime,
            Name = input.Name,
            Description = input.Description,
            DefaultApplication = input.DefaultApplication,
            DefaultPlatform = input.DefaultPlatform,
            Defaults = System.Text.Json.JsonSerializer.Deserialize<Domain.ValueObjects.TemplateDefaults>(input.DefaultsJson)!,
            AppSourceCop = System.Text.Json.JsonSerializer.Deserialize<Domain.ValueObjects.AppSourceCopSettings>(input.AppSourceCopJson)!,
            CoreIdRangeFrom = input.CoreIdRangeFrom,
            CoreIdRangeTo = input.CoreIdRangeTo,
            ModuleIdRangeStart = input.ModuleIdRangeStart,
            ModuleIdRangeSize = input.ModuleIdRangeSize,
            Folders = input.Folders.Select((f, i) => new TemplateFolder
            {
                Ordering = i,
                Path = f.Path,
                Files = f.Files.Select((file, fi) => new TemplateFile
                {
                    Ordering = fi,
                    Path = file.Path,
                    Content = file.Content,
                }).ToList(),
            }).ToList(),
            ModuleFolders = input.ModuleFolders.Select((f, i) => new TemplateModuleFolder
            {
                Ordering = i,
                Path = f.Path,
                Files = f.Files.Select((file, fi) => new TemplateModuleFile
                {
                    Ordering = fi,
                    Path = file.Path,
                    Content = file.Content,
                }).ToList(),
            }).ToList(),
            DefaultModules = input.DefaultModuleKeys.Select((key, i) => new RuntimeTemplateDefaultModule
            {
                Ordering = i,
                Module = ModuleBuilder.Default(key, key),
            }).ToList(),
        };
        if (input.DefaultApplicationVersionKey is { } versionKey)
        {
            template.DefaultApplicationVersion = new ApplicationVersion
            {
                Key = versionKey,
                Name = versionKey,
                Application = input.DefaultApplication,
                Runtime = input.Runtime,
            };
        }
        return template;
    }
}
