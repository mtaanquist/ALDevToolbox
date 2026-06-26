using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The per-org allowed-source-control-providers setting on
/// <see cref="OrganizationConfigService"/>: an unconfigured org permits both, a
/// saved selection round-trips, and an empty selection is rejected with a
/// field-keyed error. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class OrganizationConfigAllowedProvidersTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Unconfigured_org_allows_all_providers()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var allowed = await svc.GetAllowedProvidersAsync();

        allowed.Should().BeEquivalentTo(new[] { RepositoryProvider.GitHub, RepositoryProvider.AzureDevOps });
        (await svc.IsProviderAllowedAsync(RepositoryProvider.GitHub)).Should().BeTrue();
        (await svc.IsProviderAllowedAsync(RepositoryProvider.AzureDevOps)).Should().BeTrue();
    }

    [Fact]
    public async Task Save_narrows_the_allowed_set_and_round_trips()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveAllowedProvidersAsync(new[] { RepositoryProvider.GitHub });

        var allowed = await svc.GetAllowedProvidersAsync();
        allowed.Should().ContainSingle().Which.Should().Be(RepositoryProvider.GitHub);
        (await svc.IsProviderAllowedAsync(RepositoryProvider.AzureDevOps)).Should().BeFalse();
    }

    [Fact]
    public async Task Empty_selection_is_rejected_with_a_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.SaveAllowedProvidersAsync(Array.Empty<RepositoryProvider>());

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("AllowedRepositoryProviders");
    }
}
