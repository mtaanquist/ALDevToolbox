using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// CRUD + validation contract for <see cref="CustomerService"/>: a customer and
/// its repositories round-trip, validation rejects blank names, duplicate names,
/// and provider/URL mismatches with field-keyed errors, update replaces the repo
/// set, soft-delete hides the row, and the org query filter keeps customers from
/// other orgs invisible.
/// </summary>
public sealed class CustomerServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CustomerInput NewInput(
        string name = "Acme",
        string? country = "dk",
        params CustomerRepositoryInput[] repos)
        => new(name, country, repos.Length == 0
            ? new[] { new CustomerRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core") }
            : repos);

    [Fact]
    public async Task Create_persists_customer_and_repositories()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);

        var id = await svc.CreateCustomerAsync(NewInput(
            "Acme A/S", "dk",
            new CustomerRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core"),
            new CustomerRepositoryInput(RepositoryProvider.AzureDevOps, "https://dev.azure.com/acme/bc/_git/exts", "Exts")));

        var loaded = await svc.GetCustomerAsync(id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Acme A/S");
        loaded.DefaultArtifactCountry.Should().Be("dk");
        loaded.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        loaded.Repositories.Should().HaveCount(2);
        loaded.Repositories.Should().Contain(r => r.Provider == RepositoryProvider.GitHub && r.DisplayName == "Core");
        loaded.Repositories.Should().OnlyContain(r => r.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task Create_defaults_display_name_to_repo_slug_when_blank()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);

        var id = await svc.CreateCustomerAsync(NewInput("Acme", null,
            new CustomerRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core.git", "")));

        var loaded = await svc.GetCustomerAsync(id);
        loaded!.Repositories.Single().DisplayName.Should().Be("core");
        loaded.DefaultArtifactCountry.Should().BeNull("blank country is stored as null");
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);

        var act = () => svc.CreateCustomerAsync(NewInput(name: "   "));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Theory]
    [InlineData(RepositoryProvider.AzureDevOps, "https://github.com/acme/core")]
    [InlineData(RepositoryProvider.GitHub, "https://dev.azure.com/acme/bc/_git/core")]
    [InlineData(RepositoryProvider.GitHub, "http://github.com/acme/core")] // not https
    [InlineData(RepositoryProvider.GitHub, "not-a-url")]
    public async Task Create_rejects_provider_url_mismatch(RepositoryProvider provider, string url)
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);

        var act = () => svc.CreateCustomerAsync(NewInput("Acme", "dk",
            new CustomerRepositoryInput(provider, url, "Repo")));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Repositories[0].Url");
    }

    [Fact]
    public async Task Create_rejects_duplicate_active_name()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);
        await svc.CreateCustomerAsync(NewInput("Acme"));

        var act = () => svc.CreateCustomerAsync(NewInput("acme")); // case-insensitive clash

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Update_replaces_repository_set()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);
        var id = await svc.CreateCustomerAsync(NewInput("Acme", "dk",
            new CustomerRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/old", "Old")));

        await svc.UpdateCustomerAsync(id, new CustomerInput("Acme Renamed", "w1", new[]
        {
            new CustomerRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/new", "New"),
        }));

        var loaded = await svc.GetCustomerAsync(id);
        loaded!.Name.Should().Be("Acme Renamed");
        loaded.DefaultArtifactCountry.Should().Be("w1");
        loaded.Repositories.Should().ContainSingle().Which.DisplayName.Should().Be("New");

        await using var verify = _db.NewContext();
        var orphanRepos = await verify.OeCustomerRepositories.CountAsync(r => r.Url.Contains("old"));
        orphanRepos.Should().Be(0, "the replaced repo rows are removed, not left dangling");
    }

    [Fact]
    public async Task SoftDelete_hides_customer_from_list_and_frees_the_name()
    {
        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);
        var id = await svc.CreateCustomerAsync(NewInput("Acme"));

        await svc.SoftDeleteCustomerAsync(id);

        (await svc.ListCustomersAsync()).Should().BeEmpty();
        (await svc.GetCustomerAsync(id)).Should().BeNull();
        // The soft-delete filter on the unique index frees the name for reuse.
        var act = () => svc.CreateCustomerAsync(NewInput("Acme"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Customers_from_another_org_are_invisible()
    {
        // Insert a customer owned by the other org directly (write filters don't
        // apply); the org-scoped read must not surface it.
        await using (var seed = _db.NewContext())
        {
            seed.OeCustomers.Add(new Customer
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "Other Co",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = new CustomerService(ctx, _db.OrgContext, NullLogger<CustomerService>.Instance);
        (await svc.ListCustomersAsync()).Should().BeEmpty("the query filter scopes to the acting org");
    }
}
