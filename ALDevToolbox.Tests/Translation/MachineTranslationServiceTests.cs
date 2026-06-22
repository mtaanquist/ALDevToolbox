using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Translation.Providers;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translation;

/// <summary>
/// <see cref="MachineTranslationService"/> orchestration: it stays silent (null /
/// Off) when unconfigured, calls the configured provider when set up, passes the
/// context hint through, and degrades to null — never throws — when the provider
/// fails. A fake <see cref="IMachineTranslationProviderFactory"/> stands in for
/// the network so the test is the legitimate seam the interface exists for.
/// </summary>
public sealed class MachineTranslationServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Translate_returns_null_and_never_builds_a_provider_when_off()
    {
        await using var ctx = _db.NewContext();
        var config = _db.NewOrganizationConfigService(ctx);
        var factory = new FakeFactory(new FakeProvider(new MtTranslation("Hej", "Fake", "EN")));
        var svc = new MachineTranslationService(config, factory, NullLogger<MachineTranslationService>.Instance);

        (await svc.TranslateAsync("Hello", "en-US", "da-DK", null)).Should().BeNull();
        (await svc.GetTriggerAsync()).Should().Be(MtTrigger.Off);
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task Translate_uses_provider_and_passes_context_when_configured()
    {
        await using var ctx = _db.NewContext();
        var config = _db.NewOrganizationConfigService(ctx);
        await config.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.OnDemand));
        var provider = new FakeProvider(new MtTranslation("Hej", "Fake", "EN"));
        var svc = new MachineTranslationService(config, new FakeFactory(provider), NullLogger<MachineTranslationService>.Instance);

        var result = await svc.TranslateAsync("Hello", "en-US", "da-DK", "field caption");

        result.Should().NotBeNull();
        result!.TargetText.Should().Be("Hej");
        provider.LastContext.Should().Be("field caption");
        (await svc.GetTriggerAsync()).Should().Be(MtTrigger.OnDemand);
    }

    [Fact]
    public async Task Translate_degrades_to_null_when_provider_throws()
    {
        await using var ctx = _db.NewContext();
        var config = _db.NewOrganizationConfigService(ctx);
        await config.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.AlwaysAuto));
        var svc = new MachineTranslationService(config, new FakeFactory(new ThrowingProvider()), NullLogger<MachineTranslationService>.Instance);

        (await svc.TranslateAsync("Hello", "en-US", "da-DK", null)).Should().BeNull();
    }

    [Fact]
    public async Task TestConnection_reports_not_configured_when_off()
    {
        await using var ctx = _db.NewContext();
        var config = _db.NewOrganizationConfigService(ctx);
        var svc = new MachineTranslationService(config, new FakeFactory(new ThrowingProvider()), NullLogger<MachineTranslationService>.Instance);

        var result = await svc.TestConnectionAsync();

        result.Success.Should().BeFalse();
    }

    private sealed class FakeFactory : IMachineTranslationProviderFactory
    {
        private readonly IMachineTranslationProvider _provider;
        public int CreateCount { get; private set; }
        public FakeFactory(IMachineTranslationProvider provider) => _provider = provider;
        public IMachineTranslationProvider Create(ResolvedMtSettings settings)
        {
            CreateCount++;
            return _provider;
        }
    }

    private sealed class FakeProvider : IMachineTranslationProvider
    {
        private readonly MtTranslation _result;
        public string? LastContext { get; private set; }
        public FakeProvider(MtTranslation result) => _result = result;
        public Task<MtTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new MtTestResult(true, "ok"));
        public Task<MtTranslation> TranslateAsync(string sourceText, string? sourceLanguage, string targetLanguage, string? context, CancellationToken ct)
        {
            LastContext = context;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingProvider : IMachineTranslationProvider
    {
        public Task<MtTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new MtTestResult(false, "x"));
        public Task<MtTranslation> TranslateAsync(string sourceText, string? sourceLanguage, string targetLanguage, string? context, CancellationToken ct)
            => throw new HttpRequestException("boom");
    }
}
