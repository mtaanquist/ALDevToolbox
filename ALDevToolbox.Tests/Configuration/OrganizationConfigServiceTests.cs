using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Covers the per-organisation configuration service introduced in
/// Milestone P3.14: settings round-trip, logo validation + SVG sanitisation,
/// always-included file reconciliation, and the import path. The cross-org
/// isolation expectation is exercised in <see cref="CrossOrgConfigIsolationTests"/>.
/// </summary>
public sealed class OrganizationConfigServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SaveSettings_round_trips_and_invalidates_cache()
    {
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var first = await svc.GetCurrentAsync();
            // Empty starting state — no row, the default in-memory shape leaves
            // the publisher blank.
            first.Settings.DefaultPublisher.Should().BeEmpty();

            await svc.SaveSettingsAsync(new OrganizationSettingsInput(
                DefaultPublisher: "Acme",
                DefaultIdRangeFrom: 50000,
                DefaultIdRangeTo: 50999,
                DefaultBrief: "brief",
                DefaultCoreDescription: "desc"));
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var loaded = await svc.GetCurrentAsync();
            loaded.Settings.DefaultPublisher.Should().Be("Acme");
            loaded.Settings.DefaultIdRangeFrom.Should().Be(50000);
            loaded.Settings.DefaultIdRangeTo.Should().Be(50999);
            loaded.Settings.DefaultBrief.Should().Be("brief");
            loaded.Settings.DefaultCoreDescription.Should().Be("desc");
        }
    }

    [Theory]
    [InlineData("", 50000, 50999, nameof(OrganizationSettingsInput.DefaultPublisher))]
    [InlineData("Acme", 0, 50999, nameof(OrganizationSettingsInput.DefaultIdRangeFrom))]
    [InlineData("Acme", 51000, 50999, nameof(OrganizationSettingsInput.DefaultIdRangeTo))]
    public async Task SaveSettings_rejects_invalid_input(
        string publisher, int from, int to, string expectedField)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var input = new OrganizationSettingsInput(publisher, from, to, string.Empty, string.Empty);
        var act = () => svc.SaveSettingsAsync(input);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(expectedField);
    }

    [Fact]
    public async Task UploadLogo_rejects_unknown_content_type()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.UploadLogoAsync("image/jpeg", new byte[] { 1, 2, 3 });
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("contentType");
    }

    [Fact]
    public async Task UploadLogo_rejects_oversized_payload()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var oversized = new byte[OrganizationConfigService.MaxLogoBytes + 1];
        var act = () => svc.UploadLogoAsync("image/png", oversized);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("content");
    }

    [Fact]
    public async Task UploadLogo_persists_png_bytes_unchanged()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.UploadLogoAsync("image/png", bytes);
        }
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Logo.Should().NotBeNull();
            snapshot.Logo!.ContentType.Should().Be("image/png");
            snapshot.Logo.Content.Should().Equal(bytes);
        }
    }

    [Fact]
    public void SanitiseLogo_strips_script_tags_and_event_handlers_from_svg()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)">
              <script>alert('hi')</script>
              <rect onclick="evil()" width="10" height="10" />
            </svg>
            """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(svg);
        var sanitised = OrganizationConfigService.SanitiseLogo("image/svg+xml", bytes);
        var text = System.Text.Encoding.UTF8.GetString(sanitised);
        text.Should().NotContain("<script");
        text.Should().NotContain("onload=");
        text.Should().NotContain("onclick=");
        text.Should().Contain("<rect");
        text.Should().Contain("width=\"10\"");
    }

    private static string Sanitise(string svg) =>
        System.Text.Encoding.UTF8.GetString(OrganizationConfigService.SanitiseLogo(
            "image/svg+xml", System.Text.Encoding.UTF8.GetBytes(svg)));

    [Fact]
    public void SanitiseLogo_rejects_svg_that_is_not_well_formed()
    {
        // The old blacklist regex needed a closing </script>, so an unterminated
        // opening tag went through untouched. It is not well-formed XML, and the
        // allow-list sanitiser refuses it outright rather than half-cleaning it.
        var act = () => Sanitise("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</svg>""");
        act.Should().Throw<PlanValidationException>();
    }

    [Fact]
    public void SanitiseLogo_drops_foreign_object_and_its_html_subtree()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <foreignObject width="100" height="100">
                <body xmlns="http://www.w3.org/1999/xhtml"><img src="x" onerror="alert(1)" /></body>
              </foreignObject>
              <circle cx="5" cy="5" r="5" />
            </svg>
            """);
        text.Should().NotContain("foreignObject");
        text.Should().NotContain("onerror");
        text.Should().NotContain("<img");
        text.Should().Contain("<circle");
    }

    [Fact]
    public void SanitiseLogo_drops_anchor_with_javascript_href()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <a xlink:href="javascript:alert(1)"><rect width="10" height="10" /></a>
            </svg>
            """);
        text.Should().NotContain("javascript");
        text.Should().NotContain("xlink");
        // The anchor is unwrapped: the link goes, the artwork inside stays.
        text.Should().NotContain("<a ");
        text.Should().Contain("<rect");
    }

    [Fact]
    public void SanitiseLogo_unwraps_a_linked_logo_keeping_its_artwork()
    {
        // Exporters wrap a whole group in <a> when the designer added a link.
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <a xlink:href="https://cronus.example">
                <g>
                  <path d="M0 0 L10 10" fill="#0a0" />
                  <path d="M10 10 L20 0" fill="#00a" />
                </g>
              </a>
            </svg>
            """);
        text.Should().NotContain("<a ");
        text.Should().NotContain("cronus.example");
        text.Should().Contain("<g>");
        text.Should().Contain("fill=\"#0a0\"");
        text.Should().Contain("fill=\"#00a\"");
    }

    [Fact]
    public void SanitiseLogo_drops_animation_elements()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect width="10" height="10">
                <animate attributeName="href" values="javascript:alert(1)" />
                <animateTransform attributeName="transform" type="rotate" />
                <set attributeName="href" to="javascript:alert(1)" />
              </rect>
            </svg>
            """);
        text.Should().NotContain("animate");
        text.Should().NotContain("<set");
        text.Should().NotContain("javascript");
        text.Should().Contain("<rect");
    }

    [Fact]
    public void SanitiseLogo_drops_use_image_style_and_event_attributes()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)" style="background:url(javascript:alert(1))">
              <style>* { fill: url(javascript:alert(1)); }</style>
              <use href="data:text/html,&lt;script&gt;" />
              <image href="javascript:alert(1)" />
              <path d="M0 0 L10 10" onmouseover="alert(1)" fill="#123456" />
            </svg>
            """);
        text.Should().NotContain("onload");
        text.Should().NotContain("onmouseover");
        text.Should().NotContain("style");
        text.Should().NotContain("<use");
        text.Should().NotContain("<image");
        text.Should().NotContain("javascript");
        text.Should().Contain("fill=\"#123456\"");
    }

    [Fact]
    public void SanitiseLogo_keeps_plain_css_in_style_attributes_and_blocks()
    {
        // Illustrator, Inkscape and Figma exports carry a logo's fills here, so
        // dropping them outright would render most real logos black.
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>.a{fill:#0a0}</style>
              <path class="a" d="M0 0 L10 10" style="fill:#0a0" />
            </svg>
            """);
        text.Should().Contain("style=\"fill:#0a0\"");
        text.Should().Contain(".a{fill:#0a0}");
    }

    [Fact]
    public void SanitiseLogo_drops_css_that_can_fetch_or_execute()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>@import "//evil.example/x.css";</style>
              <path d="M0 0" style="fill:url(javascript:alert(1))" />
              <rect width="10" height="10" style="fill:#0a0" />
            </svg>
            """);
        text.Should().NotContain("@import");
        text.Should().NotContain("javascript");
        text.Should().NotContain("<style");
        // Only the offending attribute goes, not the elements around it.
        text.Should().Contain("<path");
        text.Should().Contain("style=\"fill:#0a0\"");
    }

    [Fact]
    public void SanitiseLogo_keeps_a_benign_logo_intact()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24">
              <title>CRONUS</title>
              <defs>
                <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#ff0000" />
                  <stop offset="1" stop-color="#0000ff" />
                </linearGradient>
              </defs>
              <g transform="translate(1 1)">
                <path d="M0 0 L10 10 Z" fill="url(#g)" stroke="#000" stroke-width="2" />
                <circle cx="5" cy="5" r="4" fill-opacity="0.5" />
                <text x="2" y="20" font-family="Segoe UI" font-size="8">CRONUS</text>
              </g>
            </svg>
            """;
        var original = System.Xml.Linq.XDocument.Parse(svg);
        var sanitised = System.Xml.Linq.XDocument.Parse(Sanitise(svg));

        // Compare element and attribute sets rather than the exact string: the
        // sanitiser re-serialises, so whitespace and attribute order may differ.
        static IEnumerable<string> Shape(System.Xml.Linq.XDocument d) =>
            d.Descendants().Select(e =>
                e.Name.LocalName + "[" + string.Join(
                    ",",
                    e.Attributes()
                        .Where(a => !a.IsNamespaceDeclaration)
                        .Select(a => a.Name.LocalName + "=" + a.Value)
                        .Order(StringComparer.Ordinal)) + "]");

        Shape(sanitised).Should().Equal(Shape(original));
        sanitised.Root!.Attribute("viewBox")!.Value.Should().Be("0 0 24 24");
        sanitised.Root.Name.NamespaceName.Should().Be("http://www.w3.org/2000/svg");
        sanitised.Declaration.Should().BeNull();
        sanitised.Descendants().Select(e => e.Value).Should().Contain("CRONUS");
    }

    [Fact]
    public void SanitiseLogo_keeps_safe_hrefs_on_allowed_elements()
    {
        var text = Sanitise("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <clipPath id="c"><rect width="10" height="10" /></clipPath>
              <path d="M0 0" clip-path="url(#c)" />
            </svg>
            """);
        text.Should().Contain("clipPath");
        text.Should().Contain("clip-path=\"url(#c)\"");
    }

    [Fact]
    public void SanitiseLogo_leaves_png_bytes_untouched()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        OrganizationConfigService.SanitiseLogo("image/png", png).Should().Equal(png);
    }

    [Fact]
    public async Task SaveFiles_inserts_updates_and_deletes_to_match_input()
    {
        // Seed two files; replace the input with one updated and one new entry.
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveFilesAsync(new[]
            {
                new OrganizationFileInput(null, ".editorconfig", "root = true", false),
                new OrganizationFileInput(null, "README.md", "Hello", false),
            });
        }

        int editorId;
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Files.Should().HaveCount(2);
            editorId = snapshot.Files.First(f => f.Path == ".editorconfig").Id;
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveFilesAsync(new[]
            {
                new OrganizationFileInput(editorId, ".editorconfig", "root = false", true),
                new OrganizationFileInput(null, "docs/notes.md", "fresh", false),
            });
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Files.Should().HaveCount(2);
            snapshot.Files.Single(f => f.Path == ".editorconfig").Content.Should().Be("root = false");
            snapshot.Files.Single(f => f.Path == ".editorconfig").MustacheEnabled.Should().BeTrue();
            snapshot.Files.Should().Contain(f => f.Path == "docs/notes.md");
            snapshot.Files.Should().NotContain(f => f.Path == "README.md");
        }
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("absolute/.." )]
    [InlineData("with spaces/foo.txt")]
    public async Task SaveFiles_rejects_invalid_paths(string path)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.SaveFilesAsync(new[] { new OrganizationFileInput(null, path, "x", false) });
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().ContainSingle();
    }

    [Fact]
    public async Task SaveCodeWorkspaceJson_round_trips_and_isolates_from_defaults()
    {
        // Seed defaults first so we can prove SaveCodeWorkspaceJsonAsync touches
        // only its column and leaves the publisher / id range alone (Issue #61).
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveSettingsAsync(new OrganizationSettingsInput(
                "Acme", 50000, 50999, "brief", "desc"));
        }

        const string adminJson = """
            {
              "settings": {
                "al.ruleSetPath": "../my.ruleset.json"
              }
            }
            """;
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveCodeWorkspaceJsonAsync(adminJson);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var loaded = await svc.GetCurrentAsync();
            loaded.Settings.CodeWorkspaceJson.Should().Be(adminJson);
            loaded.Settings.DefaultPublisher.Should().Be("Acme");
            loaded.Settings.DefaultIdRangeFrom.Should().Be(50000);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not even close to json")]
    [InlineData("[\"array root not allowed\"]")]
    [InlineData("\"plain string\"")]
    public async Task SaveCodeWorkspaceJson_rejects_invalid_input(string bad)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.SaveCodeWorkspaceJsonAsync(bad);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("codeWorkspaceJson");
    }

    [Fact]
    public async Task GetCurrent_falls_back_to_default_workspace_json_when_no_row_persisted()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var snapshot = await svc.GetCurrentAsync();

        // No SaveSettings call has happened, so the row is transient. The
        // in-app default seeds the workspace JSON template so fresh orgs can
        // generate workspaces without an admin saving the page first.
        snapshot.Settings.CodeWorkspaceJson.Should().Be(OrganizationDefaults.CodeWorkspaceJson);
    }

    [Fact]
    public async Task ImportFromToml_replaces_settings_files_and_logo()
    {
        // Seed an initial state we expect to be wiped, then import a TOML
        // carrying a different config and verify the post-state matches.
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveSettingsAsync(new OrganizationSettingsInput("OldPub", 90000, 90999, "old", "old"));
            await svc.SaveFilesAsync(new[]
            {
                new OrganizationFileInput(null, "old.txt", "stale", false),
            });
        }

        var pngBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var toml = $$"""
            [settings]
            default_publisher = "NewPub"
            default_id_range_from = 50000
            default_id_range_to = 50999
            default_brief = "imported"
            default_core_description = "imported desc"

            [logo]
            content_type = "image/png"
            content_base64 = "{{pngBase64}}"

            [[file]]
            path = "fresh.txt"
            content = "hello"
            mustache_enabled = false
            """;

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.ImportFromTomlAsync(toml);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Settings.DefaultPublisher.Should().Be("NewPub");
            snapshot.Settings.DefaultIdRangeFrom.Should().Be(50000);
            snapshot.Files.Should().ContainSingle(f => f.Path == "fresh.txt" && f.Content == "hello");
            snapshot.Files.Should().NotContain(f => f.Path == "old.txt");
            snapshot.Logo.Should().NotBeNull();
            snapshot.Logo!.ContentType.Should().Be("image/png");
            snapshot.Logo.Content.Should().Equal(0x89, 0x50, 0x4E, 0x47);
        }
    }
}
