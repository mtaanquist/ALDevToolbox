using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The naming block of the Defaults page (#757): the two derived-name styles
/// and the extension-prefix policy, saved through
/// <see cref="OrganizationConfigService.SaveNamingAsync"/>. See
/// <c>.design/customer-naming.md</c>.
/// </summary>
public sealed class OrganizationNamingSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_fresh_organisation_starts_on_the_generators_historic_behaviour()
    {
        await using var ctx = _db.NewContext();
        var settings = (await _db.NewOrganizationConfigService(ctx).GetCurrentAsync()).Settings;

        settings.NamingFolderStyle.Should().Be(NamingStyle.PascalCase);
        settings.NamingRepositoryStyle.Should().Be(NamingStyle.KebabCase);
        settings.ExtensionPrefixMode.Should().Be(ExtensionPrefixMode.PerWorkspace);
        settings.ExtensionPrefix.Should().BeNull();
    }

    [Fact]
    public async Task SaveNaming_round_trips_and_invalidates_the_cache()
    {
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            // Read first so the cache holds the pre-save snapshot; the save has
            // to drop it or every reader keeps the old styles.
            await svc.GetCurrentAsync();
            await svc.SaveNamingAsync(new OrganizationNamingInput(
                FolderStyle: NamingStyle.Lowercase,
                RepositoryStyle: NamingStyle.SnakeCase,
                ExtensionPrefixMode: ExtensionPrefixMode.Fixed,
                ExtensionPrefix: "  PARTNER  "));

            var reread = await svc.GetCurrentAsync();
            reread.Settings.NamingFolderStyle.Should().Be(NamingStyle.Lowercase);
        }

        await using (var ctx = _db.NewContext())
        {
            var loaded = (await _db.NewOrganizationConfigService(ctx).GetCurrentAsync()).Settings;
            loaded.NamingFolderStyle.Should().Be(NamingStyle.Lowercase);
            loaded.NamingRepositoryStyle.Should().Be(NamingStyle.SnakeCase);
            loaded.ExtensionPrefixMode.Should().Be(ExtensionPrefixMode.Fixed);
            loaded.ExtensionPrefix.Should().Be("PARTNER");
        }
    }

    [Fact]
    public async Task A_blank_prefix_is_stored_as_no_prefix_at_all()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveNamingAsync(new OrganizationNamingInput(
            NamingStyle.PascalCase, NamingStyle.KebabCase, ExtensionPrefixMode.PerWorkspace, "   "));

        (await svc.GetCurrentAsync()).Settings.ExtensionPrefix.Should().BeNull();
    }

    [Fact]
    public async Task Repository_names_cannot_be_words_with_spaces()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.SaveNamingAsync(new OrganizationNamingInput(
            NamingStyle.PascalCase, NamingStyle.None, ExtensionPrefixMode.PerWorkspace));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(OrganizationNamingInput.RepositoryStyle));
    }

    [Fact]
    public async Task One_prefix_for_every_workspace_needs_a_prefix()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.SaveNamingAsync(new OrganizationNamingInput(
            NamingStyle.PascalCase, NamingStyle.KebabCase, ExtensionPrefixMode.Fixed, null));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(OrganizationNamingInput.ExtensionPrefix));
    }

    [Fact]
    public async Task A_prefix_longer_than_the_ceiling_is_refused()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var act = () => svc.SaveNamingAsync(new OrganizationNamingInput(
            NamingStyle.PascalCase,
            NamingStyle.KebabCase,
            ExtensionPrefixMode.Fixed,
            new string('X', OrganizationConfigService.MaxExtensionPrefixLength + 1)));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(OrganizationNamingInput.ExtensionPrefix));
    }
}
