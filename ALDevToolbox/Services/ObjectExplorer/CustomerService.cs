using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// CRUD over <see cref="Customer"/> and its <see cref="CustomerRepository"/>
/// children — the admin surface that defines what the customer-build pipeline
/// clones and compiles. Org-scoped via the EF query filter; mutations run inside
/// an authenticated request (<see cref="RequireOrganizationId"/> throws
/// otherwise). Validation throws <see cref="PlanValidationException"/> with
/// field-keyed errors so the form renders them inline. See
/// <c>.design/object-explorer-customer-builds.md</c>.
/// </summary>
public sealed class CustomerService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<CustomerService> _logger;

    public CustomerService(AppDbContext db, IOrganizationContext orgContext, ILogger<CustomerService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; customer mutation called outside an authenticated request.");

    /// <summary>Active (non-deleted) customers for the current org, repositories included, ordered by name.</summary>
    public async Task<List<Customer>> ListCustomersAsync(CancellationToken ct = default)
    {
        return await _db.OeCustomers
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .Include(c => c.Repositories)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>A single active customer with its repositories, or null when not found in this org.</summary>
    public async Task<Customer?> GetCustomerAsync(int id, CancellationToken ct = default)
    {
        return await _db.OeCustomers
            .AsNoTracking()
            .Where(c => c.Id == id && c.DeletedAt == null)
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The releases this customer's builds produced, newest first — linked via the
    /// import job's <see cref="ImportJob.CustomerId"/> (a customer Release carries
    /// no FK back to the customer, only a name). Drives the customer detail page's
    /// build history.
    /// </summary>
    public async Task<List<CustomerReleaseRow>> ListCustomerReleasesAsync(int customerId, CancellationToken ct = default)
    {
        var releaseIds = await _db.OeImportJobs.AsNoTracking()
            .Where(j => j.CustomerId == customerId)
            .Select(j => j.ReleaseId)
            .Distinct()
            .ToListAsync(ct);
        if (releaseIds.Count == 0) return new List<CustomerReleaseRow>();

        return await _db.OeReleases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .OrderByDescending(r => r.ImportedAt)
            .Select(r => new CustomerReleaseRow(r.Id, r.Label, r.Status, r.BcVersion, r.ImportedAt, r.DeletedAt))
            .ToListAsync(ct);
    }

    /// <summary>Creates a customer and its repositories. Returns the new id.</summary>
    public async Task<int> CreateCustomerAsync(CustomerInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, country, repos) = await ValidateAsync(input, existingId: null, orgId, ct);

        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            OrganizationId = orgId,
            Name = name,
            DefaultArtifactCountry = country,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories = repos.Select(r => new CustomerRepository
            {
                OrganizationId = orgId,
                Provider = r.Provider,
                Url = r.Url,
                DisplayName = r.DisplayName,
            }).ToList(),
        };
        _db.OeCustomers.Add(customer);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created customer {CustomerId} ({Name}) with {RepoCount} repo(s) for org {OrgId}.",
            customer.Id, name, customer.Repositories.Count, orgId);
        return customer.Id;
    }

    /// <summary>
    /// Updates a customer's name/country and replaces its repository set with the
    /// posted one (the form owns the whole list, so a save is a full replace).
    /// </summary>
    public async Task UpdateCustomerAsync(int id, CustomerInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var (name, country, repos) = await ValidateAsync(input, existingId: id, orgId, ct);

        var customer = await _db.OeCustomers
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This customer no longer exists.");

        customer.Name = name;
        customer.DefaultArtifactCountry = country;
        customer.UpdatedAt = DateTime.UtcNow;

        // Full replace: drop the old rows, add the posted set. Repos are cheap and
        // identity-free from the form's perspective, so we don't diff in place.
        _db.OeCustomerRepositories.RemoveRange(customer.Repositories);
        customer.Repositories = repos.Select(r => new CustomerRepository
        {
            OrganizationId = orgId,
            CustomerId = customer.Id,
            Provider = r.Provider,
            Url = r.Url,
            DisplayName = r.DisplayName,
        }).ToList();

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Updated customer {CustomerId} ({Name}); now {RepoCount} repo(s).",
            customer.Id, name, customer.Repositories.Count);
    }

    /// <summary>Soft-deletes a customer (its repositories ride along via the soft-delete marker).</summary>
    public async Task SoftDeleteCustomerAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var customer = await _db.OeCustomers
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw Validation("Name", "This customer no longer exists.");

        customer.DeletedAt = DateTime.UtcNow;
        customer.UpdatedAt = customer.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted customer {CustomerId}.", id);
    }

    /// <summary>
    /// Validates the input and returns the normalised name/country/repos. Throws
    /// <see cref="PlanValidationException"/> with field-keyed errors otherwise.
    /// </summary>
    private async Task<(string Name, string? Country, IReadOnlyList<CustomerRepositoryInput> Repos)> ValidateAsync(
        CustomerInput input, int? existingId, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors["Name"] = "Give the customer a name.";
        }
        else if (name.Length > 200)
        {
            errors["Name"] = "Keep the name under 200 characters.";
        }
        else
        {
            // Per-org name uniqueness among active rows (the DB enforces it too;
            // we pre-check for a friendly inline error rather than a 500).
            var clash = await _db.OeCustomers
                .AsNoTracking()
                .AnyAsync(c => c.DeletedAt == null
                               && c.Id != (existingId ?? 0)
                               && c.Name.ToLower() == name.ToLower(), ct);
            if (clash)
            {
                errors["Name"] = "Another customer already uses this name.";
            }
        }

        string? country = null;
        if (!string.IsNullOrWhiteSpace(input.DefaultArtifactCountry))
        {
            country = input.DefaultArtifactCountry.Trim().ToLowerInvariant();
            if (!CountryRegex.IsMatch(country))
            {
                errors["DefaultArtifactCountry"] = "Use a BC country code like 'dk' or 'w1'.";
            }
        }

        var repos = input.Repositories ?? Array.Empty<CustomerRepositoryInput>();
        var normalised = new List<CustomerRepositoryInput>(repos.Count);
        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            var url = (repo.Url ?? string.Empty).Trim();
            var display = (repo.DisplayName ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                errors[$"Repositories[{i}].Url"] = "Enter the repository URL.";
            }
            else if (!IsValidProviderUrl(repo.Provider, url))
            {
                errors[$"Repositories[{i}].Url"] = repo.Provider == RepositoryProvider.AzureDevOps
                    ? "Use an https Azure DevOps URL (dev.azure.com or *.visualstudio.com)."
                    : "Use an https github.com URL.";
            }
            // Default the display name to the last URL segment when the admin leaves it blank.
            if (display.Length == 0 && url.Length > 0)
            {
                display = url.TrimEnd('/').Split('/').LastOrDefault()?.Replace(".git", "") ?? url;
            }
            normalised.Add(new CustomerRepositoryInput(repo.Provider, url, display));
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return (name, country, normalised);
    }

    /// <summary>True when <paramref name="url"/> is an https URL on a host the provider serves.</summary>
    private static bool IsValidProviderUrl(RepositoryProvider provider, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return provider switch
        {
            RepositoryProvider.AzureDevOps =>
                host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal),
            RepositoryProvider.GitHub =>
                host == "github.com" || host == "www.github.com",
            _ => false,
        };
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static readonly System.Text.RegularExpressions.Regex CountryRegex =
        new("^[a-z0-9]{2,10}$", System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>Form-post shape for a customer and its repositories. The repo list is owned wholesale by the editor.</summary>
public sealed record CustomerInput(
    string Name,
    string? DefaultArtifactCountry,
    IReadOnlyList<CustomerRepositoryInput> Repositories);

/// <summary>One repository row from the customer editor.</summary>
public sealed record CustomerRepositoryInput(
    RepositoryProvider Provider,
    string Url,
    string DisplayName);

/// <summary>A release produced by one customer's builds — the customer detail page's build-history row.</summary>
public sealed record CustomerReleaseRow(int Id, string Label, string Status, string? BcVersion, DateTime ImportedAt, DateTime? DeletedAt);
