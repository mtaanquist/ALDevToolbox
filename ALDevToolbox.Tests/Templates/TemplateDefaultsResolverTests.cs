using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using FluentAssertions;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Pins <see cref="TemplateDefaultsResolver"/>: the helper that decides
/// which <c>(application, runtime)</c> pair the project-creation pages
/// should snap to when a template is selected. Two pages
/// (<c>NewWorkspace.razor</c>, <c>NewExtension.razor</c>) used to carry
/// this logic inline as duplicated <c>ResolveCuratedVersion</c> methods
/// (issue #65); pulling it into a static helper means a regression here
/// breaks one well-named test instead of two pages drifting silently.
/// </summary>
public sealed class TemplateDefaultsResolverTests
{
    // ---------- ResolveApplicationAndRuntime ----------

    [Fact]
    public void Resolve_prefers_curated_version_when_template_fk_resolves_in_catalogue()
    {
        var curated = new ApplicationVersion
        {
            Key = "bc-2026-w1",
            Application = "28.0.0.0",
            Runtime = "15.0",
        };
        var template = new RuntimeTemplate
        {
            Runtime = "14",                       // would normalise to "14.0" in fallback
            DefaultApplicationVersion = new ApplicationVersion { Key = "bc-2026-w1" },
        };
        template.Defaults.Application = "27.0.0.0"; // template free-text default (lower precedence)

        var (app, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template, new[] { curated });

        app.Should().Be("28.0.0.0");
        runtime.Should().Be("15.0");
    }

    [Fact]
    public void Resolve_falls_back_to_template_defaults_when_template_has_no_curated_fk()
    {
        var template = new RuntimeTemplate
        {
            Runtime = "15.2",
            DefaultApplicationVersion = null,
        };
        template.Defaults.Application = "27.5.0.0";

        var (app, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(
            template, new List<ApplicationVersion>());

        app.Should().Be("27.5.0.0");
        runtime.Should().Be("15.2");
    }

    [Fact]
    public void Resolve_falls_back_when_curated_key_is_missing_from_catalogue()
    {
        // Template still points at a curated version (FK is live) but the
        // catalogue entry has been soft-deleted / filtered out — the page
        // loaded an empty or partial list. Snap to template defaults so
        // the form still produces a sensible app.json.
        var template = new RuntimeTemplate
        {
            Runtime = "16",
            DefaultApplicationVersion = new ApplicationVersion { Key = "bc-stale" },
        };
        template.Defaults.Application = "29.0.0.0";

        var catalogue = new[]
        {
            new ApplicationVersion { Key = "bc-other", Application = "30.0.0.0", Runtime = "16.0" },
        };

        var (app, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template, catalogue);

        app.Should().Be("29.0.0.0");
        runtime.Should().Be("16.0");
    }

    [Fact]
    public void Resolve_falls_back_when_applicationVersions_list_is_null()
    {
        var template = new RuntimeTemplate
        {
            Runtime = "17",
            DefaultApplicationVersion = new ApplicationVersion { Key = "bc-2026-w2" },
        };
        template.Defaults.Application = "31.0.0.0";

        var (app, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template, applicationVersions: null);

        app.Should().Be("31.0.0.0");
        runtime.Should().Be("17.0");
    }

    [Fact]
    public void Resolve_keeps_runtime_unchanged_when_already_major_minor_in_fallback()
    {
        var template = new RuntimeTemplate { Runtime = "15.3", DefaultApplicationVersion = null };
        template.Defaults.Application = "27.0.0.0";

        var (_, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template, null);

        runtime.Should().Be("15.3");
    }

    // ---------- ResolveCuratedVersion ----------

    [Fact]
    public void ResolveCuratedVersion_returns_null_when_template_has_no_fk()
    {
        var template = new RuntimeTemplate { Runtime = "15", DefaultApplicationVersion = null };
        var catalogue = new[]
        {
            new ApplicationVersion { Key = "bc-2026-w1", Application = "28.0.0.0", Runtime = "15.0" },
        };

        TemplateDefaultsResolver.ResolveCuratedVersion(template, catalogue).Should().BeNull();
    }

    [Fact]
    public void ResolveCuratedVersion_returns_null_when_catalogue_is_null()
    {
        var template = new RuntimeTemplate
        {
            Runtime = "15",
            DefaultApplicationVersion = new ApplicationVersion { Key = "bc-2026-w1" },
        };

        TemplateDefaultsResolver.ResolveCuratedVersion(template, applicationVersions: null).Should().BeNull();
    }

    [Fact]
    public void ResolveCuratedVersion_returns_entry_matching_template_fk_key()
    {
        var match = new ApplicationVersion { Key = "bc-2026-w1", Application = "28.0.0.0", Runtime = "15.0" };
        var template = new RuntimeTemplate
        {
            Runtime = "15",
            DefaultApplicationVersion = new ApplicationVersion { Key = "bc-2026-w1" },
        };

        var result = TemplateDefaultsResolver.ResolveCuratedVersion(
            template,
            new[] { new ApplicationVersion { Key = "bc-2025-w2" }, match });

        result.Should().BeSameAs(match);
    }

    // Runtime normalisation is a private helper; pin both branches via
    // the public ResolveApplicationAndRuntime contract instead.

    [Theory]
    [InlineData("15", "15.0")]
    [InlineData("28", "28.0")]
    public void Resolve_normalises_major_only_runtime_to_major_minor_in_fallback(string templateRuntime, string expected)
    {
        var template = new RuntimeTemplate { Runtime = templateRuntime, DefaultApplicationVersion = null };
        template.Defaults.Application = "27.0.0.0";

        var (_, runtime) = TemplateDefaultsResolver.ResolveApplicationAndRuntime(template, null);

        runtime.Should().Be(expected);
    }
}
