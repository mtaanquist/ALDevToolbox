using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Translation;

/// <summary>
/// Round-trip for the per-org machine-translation settings on
/// <see cref="OrganizationConfigService"/>: the API key is encrypted, the view
/// reports it as stored without exposing it, clearing removes it, and enabling
/// without a key is rejected with a field-keyed error.
/// </summary>
public sealed class OrganizationConfigMachineTranslationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Save_encrypts_key_and_view_reports_stored_without_exposing_it()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "secret-key:fx", false, MtTrigger.OnDemand));

        var view = await svc.GetMachineTranslationViewAsync();
        view.Trigger.Should().Be(MtTrigger.OnDemand);
        view.Provider.Should().Be("deepl");
        view.HasApiKey.Should().BeTrue();

        await using var verify = _db.NewContext();
        var stored = await verify.OrganizationSettings
            .Where(s => s.OrganizationId == TestDb.DefaultOrgId)
            .Select(s => s.MachineTranslationApiKeyEncrypted)
            .FirstAsync();
        stored.Should().NotBeNullOrEmpty();
        stored.Should().NotContain("secret-key", "the column stores ciphertext, never the plaintext key");
    }

    [Fact]
    public async Task Resolve_returns_settings_after_save_and_null_when_off()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        (await svc.ResolveMachineTranslationAsync()).Should().BeNull("nothing is configured by default");

        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.AlwaysAuto));
        var resolved = await svc.ResolveMachineTranslationAsync();
        resolved.Should().NotBeNull();
        resolved!.ApiKey.Should().Be("k:fx");
        resolved.Trigger.Should().Be(MtTrigger.AlwaysAuto);
        resolved.Provider.Should().Be("deepl");

        // Off disables resolution even though a key is still stored.
        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", null, false, MtTrigger.Off));
        (await svc.ResolveMachineTranslationAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Enabling_without_a_key_is_rejected_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", null, false, MtTrigger.OnDemand));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("MachineTranslationApiKey");
    }

    [Fact]
    public async Task Empty_key_keeps_the_stored_one()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.OnDemand));

        // Re-saving with a blank key (the form posts blank to keep it) must not wipe it.
        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", null, false, MtTrigger.AlwaysAuto));

        var resolved = await svc.ResolveMachineTranslationAsync();
        resolved.Should().NotBeNull();
        resolved!.ApiKey.Should().Be("k:fx");
        resolved.Trigger.Should().Be(MtTrigger.AlwaysAuto);
    }

    [Fact]
    public async Task Clear_key_removes_stored_value()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.OnDemand));

        await svc.SaveMachineTranslationAsync(new MtSettingsInput("deepl", null, true, MtTrigger.Off));

        (await svc.GetMachineTranslationViewAsync()).HasApiKey.Should().BeFalse();
    }
}
