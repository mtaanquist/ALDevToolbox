using System.Text.Json;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Tolerance tests for <see cref="TemplateTomlMapper.FromToml"/>: real-world
/// authored TOML often carries fields that the current schema doesn't model
/// — leftovers from the pre-unified shape, deployment-specific extras, or
/// pasted-in `[workspace]` blocks. The mapper must drop the unknowns and
/// surface the values that *are* modelled, not throw.
/// </summary>
public sealed class TemplateTomlMapperToleranceTests
{
    /// <summary>
    /// A real customer-style template carried over from the pre-unified
    /// branch: Core + Hotfix + a module-cloned Document Capture extension,
    /// each with its own folder tree. Also carries three artefacts the new
    /// schema doesn't model — <c>[template].default_application</c>,
    /// <c>[template].default_platform</c>, the <c>[[defaults.modules]]</c>
    /// array, and a <c>[workspace]</c> block — to pin that the mapper
    /// tolerates them silently rather than refusing the whole document.
    /// </summary>
    private const string CustomerTemplateToml = """"
[template]
key = "runtime-new"
runtime = "0"
name = ""
default_application = ""
default_platform = "1.0.0.0"
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

[[defaults.modules]]

[workspace]
content = """
{
  "folders": [
{{paths}}
  ],
  "settings": {
    "editor.formatOnSave": true,
    "editor.autoIndent": "full",
    "editor.detectIndentation": false,
    "editor.tabSize": 4,
    "editor.insertSpaces": true,
    "al.codeAnalyzers": [
      "${CodeCop}",
      "${PerTenantExtensionCop}",
      "${UICop}"
    ],
    "al.enableCodeAnalysis": true,
    "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
  }
}
"""

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
"""";

    [Fact]
    public void Customer_template_with_pre_unified_leftovers_parses_cleanly()
    {
        var parsed = TemplateTomlMapper.FromToml(CustomerTemplateToml, deprecated: false);

        parsed.Key.Should().Be("runtime-new");
        // The pre-unified default_application / default_platform at the
        // [template] level are silently dropped (they live in [defaults] now);
        // the values from [defaults] should win.
        var defaults = JsonSerializer.Deserialize<TemplateDefaults>(parsed.DefaultsJson)!;
        defaults.Application.Should().Be("27.5.0.0");
        defaults.Platform.Should().Be("1.0.0.0");
        defaults.ExtensionPrefix.Should().Be("TEST");
        defaults.Affix.Should().Be("TEST");
        defaults.AffixType.Should().Be(AffixType.Prefix);
        defaults.Publisher.Should().Be("Test Publisher");
        defaults.Features.Should().Equal("TranslationFile", "NoImplicitWith");
        defaults.SupportedLocales.Should().Equal("en-US", "da-DK");

        // Three extensions with the expected paths.
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

        // Core: three root folders, with src/ carrying the full AL sub-tree.
        var core = parsed.Extensions.Single(e => e.Path == "Core");
        core.Folders.Select(f => f.Path).Should().Equal("permissionsets", "translations", "src");
        var src = core.Folders.Single(f => f.Path == "src");
        src.Folders.Select(f => f.Path).Should().Equal(
            "api", "codeunits", "enums",
            "pageextensions", "pages", "queries",
            "reportextensions", "reports",
            "tableextensions", "tables");

        // Hotfix: src/ with the codeunits + pageextensions slimmed-down sub-tree.
        var hotfix = parsed.Extensions.Single(e => e.Path == "Hotfix");
        hotfix.Folders.Single(f => f.Path == "src").Folders
            .Select(f => f.Path).Should().Equal("codeunits", "pageextensions");

        // Document Capture: same slim sub-tree, plus a module-key dependency.
        var docCapture = parsed.Extensions.Single(e => e.Path == "DocumentCapture");
        docCapture.Folders.Single(f => f.Path == "src").Folders
            .Select(f => f.Path).Should().Equal("codeunits", "pageextensions");
        docCapture.Dependencies.Should().ContainSingle()
            .Which.RefModuleKey.Should().Be("continia-doc-capture");
    }
}
