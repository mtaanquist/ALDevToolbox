using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using FluentAssertions;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Round-trip coverage for <see cref="TemplateTomlMapper"/> under the
/// unified-extensions model. A template goes
/// <c>RuntimeTemplate → ToToml → FromToml → TemplateAuthoring</c> and we
/// assert that every authoring field a user would care about survives the
/// trip.
/// </summary>
public sealed class TemplateTomlMapperRoundTripTests
{
    [Fact]
    public void Round_trip_preserves_metadata_and_id_ranges()
    {
        var template = TemplateBuilder.Default("runtime-15", "15.2");
        template.Name = "Runtime 15+";
        template.Description = "BC SaaS template.";
        template.CoreIdRangeFrom = 80000;
        template.CoreIdRangeTo = 80999;
        template.ModuleIdRangeStart = 81000;
        template.ModuleIdRangeSize = 250;

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Key.Should().Be("runtime-15");
        parsed.Runtime.Should().Be("15.2");
        parsed.Name.Should().Be("Runtime 15+");
        parsed.Description.Should().Be("BC SaaS template.");
        parsed.CoreIdRangeFrom.Should().Be(80000);
        parsed.CoreIdRangeTo.Should().Be(80999);
        parsed.ModuleIdRangeStart.Should().Be(81000);
        parsed.ModuleIdRangeSize.Should().Be(250);
    }

    [Fact]
    public void Round_trip_preserves_defaults_including_affix_and_extension_prefix()
    {
        var template = TemplateBuilder.Default();
        template.Defaults.Publisher = "Acme";
        template.Defaults.Application = "27.0.0.0";
        template.Defaults.Platform = "1.0.0.0";
        template.Defaults.ExtensionPrefix = "ACME";
        template.Defaults.Affix = "ACME";
        template.Defaults.AffixType = AffixType.Prefix;
        template.Defaults.Features = new List<string> { "TranslationFile", "NoImplicitWith" };
        template.Defaults.SupportedLocales = new List<string> { "en-US", "da-DK" };

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        // The mapper serialises [defaults] back into JSON; round-trip via the
        // same value-object converter the service path uses.
        var defaults = JsonSerializer.Deserialize<TemplateDefaults>(parsed.DefaultsJson)!;
        defaults.Publisher.Should().Be("Acme");
        defaults.Application.Should().Be("27.0.0.0");
        defaults.Platform.Should().Be("1.0.0.0");
        defaults.ExtensionPrefix.Should().Be("ACME");
        defaults.Affix.Should().Be("ACME");
        defaults.AffixType.Should().Be(AffixType.Prefix);
        defaults.Features.Should().Equal("TranslationFile", "NoImplicitWith");
        defaults.SupportedLocales.Should().Equal("en-US", "da-DK");
    }

    [Fact]
    public void Round_trip_preserves_extension_ordering_and_required_flag()
    {
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().NameTemplate = "{{extension_prefix}} Core";
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Hotfix",
            NameTemplate = "{{extension_prefix}} Hotfix",
            Required = false,
            Ordering = 1,
        });

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Extensions.Should().HaveCount(2);
        parsed.Extensions[0].Path.Should().Be("Core");
        parsed.Extensions[0].NameTemplate.Should().Be("{{extension_prefix}} Core");
        parsed.Extensions[0].Required.Should().BeTrue();
        parsed.Extensions[1].Path.Should().Be("Hotfix");
        parsed.Extensions[1].Required.Should().BeFalse();
    }

    [Fact]
    public void Round_trip_preserves_recursive_folder_tree_with_files_at_any_depth()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src/codeunits",
                ("AppInstall.al", "codeunit 90000 \"{{affix}} App Install\"\n{\n    Subtype = Install;\n}\n"))
            .WithCoreFolder("Permissions");
        // Attach a file at the intermediate src/ depth too — files can attach
        // at any level under the new model.
        var core = template.WorkspaceExtensions.Single();
        var src = core.Folders.Single(f => f.Path == "src");
        src.Files.Add(new WorkspaceExtensionFile
        {
            OrganizationId = template.OrganizationId,
            Path = "Readme.txt",
            Content = "Marker at src/ root.\n",
            Ordering = 0,
        });

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Extensions.Single().Folders.Should().HaveCount(2);
        var srcParsed = parsed.Extensions.Single().Folders.Single(f => f.Path == "src");
        srcParsed.Files.Should().ContainSingle().Which.Path.Should().Be("Readme.txt");
        srcParsed.Folders.Should().ContainSingle().Which.Path.Should().Be("codeunits");
        var leafFile = srcParsed.Folders.Single().Files.Single();
        leafFile.Path.Should().Be("AppInstall.al");
        leafFile.Content.Should().Contain("{{affix}} App Install");
    }

    [Fact]
    public void Round_trip_preserves_is_example_flag()
    {
        var template = TemplateBuilder.Default()
            .WithCoreFolder("src",
                ("Real.al", "// real\n"),
                ("Sample.al", "// sample\n"));
        var core = template.WorkspaceExtensions.Single();
        var src = core.Folders.Single();
        src.Files.Single(f => f.Path == "Sample.al").IsExample = true;

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        var files = parsed.Extensions.Single().Folders.Single().Files;
        files.Single(f => f.Path == "Real.al").IsExample.Should().BeFalse();
        files.Single(f => f.Path == "Sample.al").IsExample.Should().BeTrue();
    }

    [Fact]
    public void Round_trip_preserves_dependency_reference_shapes()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        // One of each reference shape.
        core.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            Ordering = 0,
            RefExtensionPath = "Hotfix",
        });
        core.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            Ordering = 1,
            RefModuleKey = "system-application",
        });
        core.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            Ordering = 2,
            LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
            LitName = "System Application",
            LitPublisher = "Microsoft",
            LitVersion = "27.0.0.0",
        });

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        var deps = parsed.Extensions.Single().Dependencies;
        deps.Should().HaveCount(3);
        deps[0].RefExtensionPath.Should().Be("Hotfix");
        deps[0].RefModuleKey.Should().BeNull();
        deps[0].LitId.Should().BeNull();
        deps[1].RefModuleKey.Should().Be("system-application");
        deps[1].RefExtensionPath.Should().BeNull();
        deps[1].LitId.Should().BeNull();
        deps[2].LitId.Should().Be("63ca2fa4-4f03-4f2b-a480-172fef340d3f");
        deps[2].LitName.Should().Be("System Application");
        deps[2].LitPublisher.Should().Be("Microsoft");
        deps[2].LitVersion.Should().Be("27.0.0.0");
        deps[2].RefExtensionPath.Should().BeNull();
        deps[2].RefModuleKey.Should().BeNull();
    }

    [Fact]
    public void Round_trip_preserves_default_modules_in_order()
    {
        var template = TemplateBuilder.Default();
        template.DefaultModules = new List<RuntimeTemplateDefaultModule>
        {
            new() { Ordering = 0, Module = new Module { Key = "alpha", Name = "Alpha" } },
            new() { Ordering = 1, Module = new Module { Key = "beta", Name = "Beta" } },
        };

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.DefaultModuleKeys.Should().Equal("alpha", "beta");
    }

    [Fact]
    public void Round_trip_preserves_per_extension_id_range_overrides()
    {
        var template = TemplateBuilder.Default();
        var core = template.WorkspaceExtensions.Single();
        core.IdRangeFrom = 70000;
        core.IdRangeTo = 70999;

        var toml = TemplateTomlMapper.ToToml(template);
        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Extensions.Single().IdRangeFrom.Should().Be(70000);
        parsed.Extensions.Single().IdRangeTo.Should().Be(70999);
    }

    [Fact]
    public void Bare_runtime_value_in_seed_toml_is_parsed_as_string()
    {
        // Backwards compat: older seed files used `runtime = 15` without
        // quotes. The mapper's normalize step wraps it so Tomlyn can
        // deserialise into the string runtime field.
        const string toml = """
            [template]
            key = "runtime-old"
            runtime = 15
            name = "Older runtime"
            core_id_range_from = 90000
            core_id_range_to = 90999
            module_id_range_start = 91000
            module_id_range_size = 200
            """;

        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Runtime.Should().Be("15");
    }

    [Fact]
    public void Blank_toml_parses_into_a_teaching_starter()
    {
        var toml = TemplateTomlMapper.BlankToml();

        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);

        parsed.Key.Should().Be("runtime-new");
        parsed.Runtime.Should().Be("26");

        // One required Core extension is the only uncommented [[extensions]]
        // entry. The Hotfix example next to it is commented out so admins
        // see the optional-extension pattern without having to delete a
        // second live entry on day one.
        parsed.Extensions.Should().ContainSingle();
        var core = parsed.Extensions.Single();
        core.Path.Should().Be("Core");
        core.Required.Should().BeTrue();

        // Starter shows: src/codeunits as a nested folder example, a
        // commented-block sibling for permissionsets / translations.
        core.Folders.Should().Contain(f => f.Path == "src");
        var src = core.Folders.Single(f => f.Path == "src");
        src.Folders.Should().ContainSingle(f => f.Path == "codeunits");
        // The nested codeunits folder ships an example .al file so the
        // mustache substitutions ({{affix}}, {{name}}) are discoverable
        // from the starter.
        src.Folders.Single().Files.Should().ContainSingle(f => f.Path == "Hello.al")
            .Which.Content.Should().Contain("{{affix}}");
    }

    [Fact]
    public void Malformed_toml_surfaces_line_aware_diagnostics()
    {
        const string toml = "this is = not = valid";

        var act = () => TemplateTomlMapper.FromToml(toml, deprecated: false);

        var ex = act.Should().Throw<TomlParseException>().Which;
        ex.Issues.Should().NotBeEmpty();
        ex.Issues.First().Line.Should().Be(1);
    }

    [Fact]
    public void Round_trip_preserves_per_template_workspace_settings_overlay()
    {
        // Issue #61 per-template overlay: the JSON the admin pastes survives
        // a ToToml → FromToml round trip verbatim, including the embedded
        // double-quotes and newlines inside the multi-line literal.
        const string overlay = """
            {
              "settings": {
                "al.newAnalyzer": "${X}",
                "al.ruleSetPath": "../my.ruleset.json"
              }
            }
            """;
        var template = TemplateBuilder.Default();
        template.CodeWorkspaceJson = overlay;

        var toml = TemplateTomlMapper.ToToml(template);
        toml.Should().Contain("[workspace_settings]");
        toml.Should().Contain("json = '''");

        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);
        parsed.CodeWorkspaceJson.Should().NotBeNull();
        // Compare the structural shape rather than whitespace — Tomlyn may
        // trim trailing whitespace on the literal and the test should tolerate
        // that without becoming brittle.
        var roundTripped = JsonDocument.Parse(parsed.CodeWorkspaceJson!).RootElement;
        roundTripped.GetProperty("settings").GetProperty("al.newAnalyzer").GetString()
            .Should().Be("${X}");
        roundTripped.GetProperty("settings").GetProperty("al.ruleSetPath").GetString()
            .Should().Be("../my.ruleset.json");
    }

    [Fact]
    public void Templates_without_overlay_emit_no_workspace_settings_block()
    {
        // Default state: no template-level overlay. ToToml must not emit the
        // [workspace_settings] block at all, and FromToml must produce a null
        // CodeWorkspaceJson on the authoring shape so the form renders "no
        // override" rather than an empty JSON blob.
        var template = TemplateBuilder.Default();
        template.CodeWorkspaceJson = null;

        var toml = TemplateTomlMapper.ToToml(template);
        toml.Should().NotContain("[workspace_settings]");

        var parsed = TemplateTomlMapper.FromToml(toml, deprecated: false);
        parsed.CodeWorkspaceJson.Should().BeNull();
    }
}
