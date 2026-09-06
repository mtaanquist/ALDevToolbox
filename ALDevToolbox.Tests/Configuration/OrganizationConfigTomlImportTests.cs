using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The wipe-and-replace TOML restore on
/// <see cref="OrganizationConfigTomlImporter"/>: settings, always-included
/// files and the logo are all replaced by what the TOML carries. The full
/// export-then-import round trip lives in <see cref="ConfigExportImportRoundTripTests"/>.
/// </summary>
public sealed class OrganizationConfigTomlImportTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

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
            var importer = _db.NewOrganizationConfigTomlImporter(ctx);
            await importer.ImportFromTomlAsync(toml);
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
