using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Coordinates starting a customer build: create the <c>ingesting</c> customer
/// Release row synchronously (so it shows in the list immediately) and enqueue a
/// <see cref="ReleaseImportSource.CustomerBuild"/> job for the worker to clone /
/// compile / ingest off-thread. Mirrors <see cref="ArtifactReleaseImporter"/>; the
/// heavy lifting lives in <see cref="CustomerBuildService"/>, run by
/// <see cref="ReleaseImportWorker"/>.
///
/// <para>
/// The Release starts with a provisional label — <c>"{Customer} (building…)"</c> —
/// because the real BC version isn't known until the build reads the repos'
/// <c>app.json</c>. The build service finalises the label once it resolves the
/// target version. Always <c>customer</c> kind.
/// </para>
/// </summary>
public sealed class CustomerReleaseImporter
{
    private readonly ReleaseImportService _importer;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<CustomerReleaseImporter> _logger;

    public CustomerReleaseImporter(
        ReleaseImportService importer,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        AppDbContext db,
        IOrganizationContext orgContext,
        ILogger<CustomerReleaseImporter> logger)
    {
        _importer = importer;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// Creates an ingesting customer Release for <paramref name="customerId"/> and
    /// queues its build. Throws <see cref="PlanValidationException"/> when the
    /// customer doesn't exist (or has no repositories) so the trigger UI can show
    /// the reason inline.
    /// </summary>
    public async Task<int> StartBuildAsync(int customerId, CancellationToken ct = default)
    {
        var customer = await _db.OeCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.DeletedAt == null)
            .Select(c => new { c.Name, RepoCount = c.Repositories.Count })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Customer"] = "This customer no longer exists.",
            });
        if (customer.RepoCount == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Customer"] = "Add at least one repository to this customer before building.",
            });
        }

        // A label unique enough to pass BeginReleaseAsync's reservation while the
        // build runs; CustomerBuildService rewrites it to the final
        // "{Customer} on BC {Major}.{Minor}" once the target version is known.
        // The timestamp keeps a re-build of the same customer from colliding with
        // a still-ingesting earlier attempt.
        var provisionalLabel = $"{customer.Name} (building… {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss})";
        var metadata = new ReleaseImportMetadata(
            Label: provisionalLabel,
            Kind: "customer",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            CustomerName: customer.Name);
        var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);

        var identity = CaptureIdentity();
        var source = new ReleaseImportSource.CustomerBuild(customerId);
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference: false, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, StoreSymbolReference: false, jobRowId), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued customer build for {Customer} (customer {CustomerId}, release {ReleaseId}).",
            customer.Name, customerId, releaseId);
        return releaseId;
    }

    private AmbientOrganizationScope.OrganizationIdentity CaptureIdentity() => new(
        OrganizationId: _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope when queuing a customer build."),
        UserId: _orgContext.CurrentUserId,
        IsSiteAdmin: _orgContext.IsSiteAdmin,
        IsSystemOrganization: _orgContext.IsSystemOrganization);
}
