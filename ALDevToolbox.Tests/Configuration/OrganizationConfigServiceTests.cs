using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Covers the per-organisation configuration service introduced in
/// Milestone P3.14: settings round-trip, the workspace JSON, and
/// always-included file reconciliation. Logo handling moved to
/// <see cref="OrganizationBrandingServiceTests"/> and the TOML import to
/// <see cref="OrganizationConfigTomlImportTests"/>. The cross-org
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
}
