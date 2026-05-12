using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

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
