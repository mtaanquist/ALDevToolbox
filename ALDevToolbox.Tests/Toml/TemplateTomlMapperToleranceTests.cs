using System.Text.Json;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Pins <see cref="TemplateTomlMapper.FromToml"/> against a real-world
/// customer-style template: three extensions (required Core, required Hotfix,
/// module-cloned Document Capture), each with its own folder tree, plus a
/// module-key dependency on the catalogue's <c>continia-doc-capture</c>.
/// Mirrors the shape an admin would actually paste into the TOML editor
/// once the unified-extensions model is in production.
/// </summary>
public sealed class TemplateTomlMapperToleranceTests
{
    private const string CustomerTemplateToml = """
[template]
key = "runtime-new"
runtime = "0"
name = ""
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200
is_default = false

[defaults]
publisher = "Test Publisher"
target = "Cloud"
features = ["TranslationFile","NoImplicitWith"]
supportedLocales = ["en-US","da-DK"]
affix = "TEST"
affixType = "Prefix"
extension_prefix = "TEST"
application = "27.5.0.0"
platform = "1.0.0.0"

[defaults.resourceExposurePolicy]
allowDebugging = false
allowDownloadingSource = false
includeSourceInSymbolFile = false

[[extensions]]
name = "{{extension_prefix}} Core"
path = "Core"

[[extensions.folders]]
path = "permissionsets"

[[extensions.folders]]
path = "translations"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "api"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders]]
path = "enums"

[[extensions.folders.folders]]
path = "pageextensions"

[[extensions.folders.folders]]
path = "pages"

[[extensions.folders.folders]]
path = "queries"

[[extensions.folders.folders]]
path = "reportextensions"

[[extensions.folders.folders]]
path = "reports"

[[extensions.folders.folders]]
path = "tableextensions"

[[extensions.folders.folders]]
path = "tables"

[[extensions]]
name = "{{extension_prefix}} Hotfix"
path = "Hotfix"

[[extensions.folders]]
path = "permissionsets"

[[extensions.folders]]
path = "translations"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders]]
path = "pageextensions"

[[extensions]]
name = "{{extension_prefix}} Document Capture"
path = "DocumentCapture"

[[extensions.dependencies]]
module = "continia-doc-capture"

[[extensions.folders]]
path = "permissionsets"

[[extensions.folders]]
path = "translations"

[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders]]
path = "pageextensions"
""";

    [Fact]
    public void Customer_template_parses_metadata_and_defaults()
    {
        var parsed = TemplateTomlMapper.FromToml(CustomerTemplateToml, deprecated: false);

        parsed.Key.Should().Be("runtime-new");
        var defaults = JsonSerializer.Deserialize<TemplateDefaults>(parsed.DefaultsJson)!;
        defaults.Application.Should().Be("27.5.0.0");
        defaults.Platform.Should().Be("1.0.0.0");
        defaults.ExtensionPrefix.Should().Be("TEST");
        defaults.Affix.Should().Be("TEST");
        defaults.AffixType.Should().Be(AffixType.Prefix);
        defaults.Publisher.Should().Be("Test Publisher");
        defaults.Features.Should().Equal("TranslationFile", "NoImplicitWith");
        defaults.SupportedLocales.Should().Equal("en-US", "da-DK");
    }

    [Fact]
    public void Customer_template_parses_three_extensions_with_substituting_name_templates()
    {
        var parsed = TemplateTomlMapper.FromToml(CustomerTemplateToml, deprecated: false);

        parsed.Extensions.Should().HaveCount(3);
        parsed.Extensions.Select(e => e.Path).Should().Equal("Core", "Hotfix", "DocumentCapture");
        parsed.Extensions.Select(e => e.NameTemplate).Should().Equal(
            "{{extension_prefix}} Core",
            "{{extension_prefix}} Hotfix",
            "{{extension_prefix}} Document Capture");
    }

    [Fact]
    public void Customer_template_preserves_recursive_folder_layout_under_each_extension()
    {
        var parsed = TemplateTomlMapper.FromToml(CustomerTemplateToml, deprecated: false);

        var core = parsed.Extensions.Single(e => e.Path == "Core");
        core.Folders.Select(f => f.Path).Should().Equal("permissionsets", "translations", "src");
        // Per TOML semantics, [[extensions.folders.folders]] appends to the most
        // recently opened [[extensions.folders]] — so api/codeunits/… all nest
        // under src/, not at the extension root.
        core.Folders.Single(f => f.Path == "src").Folders
            .Select(f => f.Path).Should().Equal(
                "api", "codeunits", "enums",
                "pageextensions", "pages", "queries",
                "reportextensions", "reports",
                "tableextensions", "tables");

        var hotfix = parsed.Extensions.Single(e => e.Path == "Hotfix");
        hotfix.Folders.Single(f => f.Path == "src").Folders
            .Select(f => f.Path).Should().Equal("codeunits", "pageextensions");

        var docCapture = parsed.Extensions.Single(e => e.Path == "DocumentCapture");
        docCapture.Folders.Single(f => f.Path == "src").Folders
            .Select(f => f.Path).Should().Equal("codeunits", "pageextensions");
        docCapture.Dependencies.Should().ContainSingle()
            .Which.RefModuleKey.Should().Be("continia-doc-capture");
    }
}
