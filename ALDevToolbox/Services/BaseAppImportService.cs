using System.IO.Compression;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Al;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Imports a Microsoft Base Application source ZIP into the
/// <c>base_app_versions</c> / <c>base_app_files</c> tables. Streams the ZIP
/// from the request body (no buffering to disk), parses each .al entry with
/// <see cref="AlDeclarationParser"/>, and inserts in batches with the change
/// tracker cleared between batches so a 10K-file import keeps memory flat.
/// </summary>
public class BaseAppImportService
{
    public const int MinMajor = 1;
    public const int MaxMajor = 999;
    public const int MinCu = 0;
    public const int MaxCu = 99;
    public const int MaxNotesLength = 4000;
    public const int BatchSize = 500;

    private readonly AppDbContext _db;
    private readonly ILogger<BaseAppImportService> _logger;
    private readonly IOrganizationContext _orgContext;
    private readonly SymbolReindexQueue? _reindexQueue;

    public BaseAppImportService(
        AppDbContext db,
        ILogger<BaseAppImportService> logger,
        IOrganizationContext orgContext,
        SymbolReindexQueue? reindexQueue = null)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
        _reindexQueue = reindexQueue;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Imports the supplied ZIP. When a row with the same (org, major, minor,
    /// cu) already exists, behaviour follows <see cref="BaseAppImportRequest.Mode"/>:
    /// <see cref="BaseAppImportMode.Reject"/> raises <see cref="PlanValidationException"/>,
    /// <see cref="BaseAppImportMode.Replace"/> soft-deletes the old version and
    /// starts fresh, and <see cref="BaseAppImportMode.Append"/> adds the ZIP's
    /// files to the existing version (replacing any rows with the same path).
    /// </summary>
    public async Task<BaseAppImportSummary> ImportAsync(
        Stream zipStream, BaseAppImportRequest request, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var startedAt = DateTime.UtcNow;
        var existingVersion = await ValidateAsync(request, orgId, ct);

        BaseAppVersion version;
        var isAppend = request.Mode == BaseAppImportMode.Append && existingVersion is not null;
        if (isAppend)
        {
            version = existingVersion!;
        }
        else
        {
            version = new BaseAppVersion
            {
                OrganizationId = orgId,
                Major = request.Major,
                CumulativeUpdate = request.CumulativeUpdate,
                ApplicationVersionId = request.ApplicationVersionId,
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes!.Trim(),
                UploadedAt = startedAt,
                CreatedAt = startedAt,
                UpdatedAt = startedAt,
            };
        }

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        var failedPaths = new List<string>();
        var batch = new List<BaseAppFile>(BatchSize);
        var totalFiles = 0;
        var parsedFiles = 0;
        var replacedPaths = 0;

        var previousAutoDetect = _db.ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // SoftDeleteExistingAsync and the append-mode prep mutate tracked
            // entities; keep change tracking on for them. We only flip
            // AutoDetectChangesEnabled off afterwards, around the bulk-insert loop.
            if (request.Mode == BaseAppImportMode.Replace)
            {
                await SoftDeleteExistingAsync(orgId, request, ct);
            }

            if (isAppend)
            {
                // Refresh the FK target so the tracked version is the one we
                // hang new files off, and stamp the row as touched.
                _db.Attach(version);
                version.UpdatedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(request.Notes))
                {
                    version.Notes = string.IsNullOrWhiteSpace(version.Notes)
                        ? request.Notes!.Trim()
                        : version.Notes + "\n" + request.Notes!.Trim();
                }
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                _db.BaseAppVersions.Add(version);
                await _db.SaveChangesAsync(ct);
            }

            _db.ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                if (!entry.Name.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;

                string content;
                await using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    content = await reader.ReadToEndAsync(ct);
                }

                totalFiles++;
                var declaration = AlDeclarationParser.Parse(content);
                if (declaration is null)
                {
                    failedPaths.Add(entry.FullName);
                    continue;
                }

                parsedFiles++;

                var file = new BaseAppFile
                {
                    OrganizationId = orgId,
                    VersionId = version.Id,
                    Path = entry.FullName,
                    FileName = entry.Name,
                    Module = ExtractTopFolder(entry.FullName),
                    ObjectType = declaration.Type,
                    ObjectId = declaration.Id,
                    ObjectName = declaration.Name,
                    Namespace = declaration.Namespace,
                    Content = content,
                    LineCount = CountLines(content),
                };

                // Extract symbol declarations and attach as nav children so
                // EF cascade-inserts them when the file row lands; FileId is
                // wired via the relationship and doesn't need to be set
                // explicitly.
                foreach (var symbol in AlSymbolExtractor.Extract(content))
                {
                    file.Symbols.Add(new BaseAppSymbol
                    {
                        OrganizationId = orgId,
                        VersionId = version.Id,
                        Kind = symbol.Kind,
                        Name = symbol.Name,
                        Signature = symbol.Signature,
                        LineNumber = symbol.LineNumber,
                        ColumnStart = symbol.ColumnStart,
                        ColumnEnd = symbol.ColumnEnd,
                    });
                }

                batch.Add(file);
                if (batch.Count >= BatchSize)
                {
                    replacedPaths += await FlushBatchAsync(batch, version.Id, isAppend, ct);
                }
            }

            if (batch.Count > 0)
            {
                replacedPaths += await FlushBatchAsync(batch, version.Id, isAppend, ct);
            }

            // Recount from DB so appended versions reflect the cumulative total
            // rather than just this ZIP's contribution.
            version.FileCount = await _db.BaseAppFiles.CountAsync(f => f.VersionId == version.Id, ct);
            version.UpdatedAt = DateTime.UtcNow;
            // Symbols were extracted inline as part of the import, so the
            // version is fully indexed once SaveChanges commits.
            version.SymbolsIndexedAt = version.UpdatedAt;
            _db.BaseAppVersions.Update(version);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            _db.ChangeTracker.Clear();
        }

        var duration = DateTime.UtcNow - startedAt;
        _logger.LogInformation(
            "Imported Object Explorer version {Major}.{Cu} for org {OrgId}: {ParsedFiles}/{TotalFiles} file(s) in {Seconds:F1}s.",
            request.Major, request.CumulativeUpdate, orgId, parsedFiles, totalFiles, duration.TotalSeconds);

        return new BaseAppImportSummary(
            version.Id,
            totalFiles,
            parsedFiles,
            failedPaths.Count,
            failedPaths.Take(20).ToArray(),
            duration,
            isAppend,
            replacedPaths);
    }

    /// <summary>
    /// Marks the version as needing reindexing and pokes the
    /// <see cref="SymbolReindexer"/> so it picks the row up immediately
    /// instead of waiting for its next poll. The reindexer's existing
    /// logic does the rest — query for rows with
    /// <see cref="BaseAppVersion.SymbolsIndexedAt"/> null, drop the old
    /// symbols, re-extract from stored content, restamp the timestamp.
    /// </summary>
    public async Task RequestReindexAsync(int versionId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.BaseAppVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Version with id {versionId} was not found.",
            });

        existing.SymbolsIndexedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _reindexQueue?.Signal();

        _logger.LogInformation(
            "Queued symbol reindex for Object Explorer version {Major}.{Cu} (id={Id}).",
            existing.Major, existing.CumulativeUpdate, existing.Id);
    }

    /// <summary>
    /// Soft-deletes the version row (file rows stay; they're cascade-deleted
    /// only if the version is hard-deleted). Surfaces "delete" on the admin
    /// list page; subsequent imports for the same (org, major, minor, cu) can
    /// land because the unique index is filtered on <c>deleted_at IS NULL</c>.
    /// </summary>
    public async Task DeleteAsync(int versionId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.BaseAppVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Version with id {versionId} was not found.",
            });

        if (existing.DeletedAt is not null) return;

        var now = DateTime.UtcNow;
        existing.DeletedAt = now;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Soft-deleted Object Explorer version {Major}.{Cu} (id={Id}).",
            existing.Major, existing.CumulativeUpdate, existing.Id);
    }

    /// <summary>
    /// Flushes a batch of files. In append mode we first delete any existing
    /// rows in the same version whose paths collide with this batch, then
    /// insert. Returns the number of rows deleted (zero on a fresh import).
    /// </summary>
    private async Task<int> FlushBatchAsync(
        List<BaseAppFile> batch, int versionId, bool isAppend, CancellationToken ct)
    {
        var replaced = 0;
        if (isAppend && batch.Count > 0)
        {
            var paths = batch.Select(b => b.Path).ToArray();
            replaced = await _db.BaseAppFiles
                .Where(f => f.VersionId == versionId && paths.Contains(f.Path))
                .ExecuteDeleteAsync(ct);
        }
        _db.BaseAppFiles.AddRange(batch);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        batch.Clear();
        return replaced;
    }

    /// <summary>
    /// Validates the request and returns the existing version row, if any.
    /// Throws <see cref="PlanValidationException"/> on validation failure or
    /// when Mode = Reject and a row already exists.
    /// </summary>
    private async Task<BaseAppVersion?> ValidateAsync(
        BaseAppImportRequest request, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        if (request.Major < MinMajor || request.Major > MaxMajor)
        {
            errors[nameof(request.Major)] = $"Major must be between {MinMajor} and {MaxMajor}.";
        }
        if (request.CumulativeUpdate < MinCu || request.CumulativeUpdate > MaxCu)
        {
            errors[nameof(request.CumulativeUpdate)] = $"Cumulative update must be between {MinCu} and {MaxCu}.";
        }
        if ((request.Notes ?? string.Empty).Length > MaxNotesLength)
        {
            errors[nameof(request.Notes)] = $"Notes must be {MaxNotesLength} characters or fewer.";
        }

        if (request.ApplicationVersionId is { } appVerId)
        {
            var exists = await _db.ApplicationVersions
                .AsNoTracking()
                .AnyAsync(v => v.Id == appVerId && v.OrganizationId == orgId && v.DeletedAt == null, ct);
            if (!exists)
            {
                errors[nameof(request.ApplicationVersionId)] = "Selected application version was not found.";
            }
        }

        BaseAppVersion? existing = null;
        if (errors.Count == 0)
        {
            existing = await _db.BaseAppVersions
                .FirstOrDefaultAsync(v => v.OrganizationId == orgId
                    && v.Major == request.Major
                    && v.CumulativeUpdate == request.CumulativeUpdate
                    && v.DeletedAt == null, ct);

            if (existing is not null && request.Mode == BaseAppImportMode.Reject)
            {
                errors[nameof(request.Major)] =
                    $"Version {request.Major}.{request.CumulativeUpdate} is already imported. "
                    + "Pick 'Replace' to overwrite or 'Append' to add this ZIP's files to the existing version.";
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }

        return existing;
    }

    private async Task SoftDeleteExistingAsync(int orgId, BaseAppImportRequest request, CancellationToken ct)
    {
        var existing = await _db.BaseAppVersions
            .Where(v => v.OrganizationId == orgId
                && v.Major == request.Major
                && v.CumulativeUpdate == request.CumulativeUpdate
                && v.DeletedAt == null)
            .ToListAsync(ct);

        if (existing.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var row in existing)
        {
            row.DeletedAt = now;
            row.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private static string? ExtractTopFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var idx = path.IndexOf('/');
        if (idx <= 0) return null;
        return path.Substring(0, idx);
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        var count = 1;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n') count++;
        }
        return count;
    }
}

/// <summary>
/// Admin-supplied metadata accompanying the ZIP upload. Behaviour when the
/// (org, major, minor, cu) triple already exists is selected via
/// <see cref="Mode"/>: Reject (default), Replace (soft-delete the old version
/// and start fresh), or Append (add this ZIP's files to the existing version).
/// Append covers Microsoft's split first-party apps — Base Application and
/// System Application can land in the same logical BC version row.
/// </summary>
public sealed record BaseAppImportRequest(
    int Major,
    int CumulativeUpdate,
    int? ApplicationVersionId,
    string? Notes,
    BaseAppImportMode Mode = BaseAppImportMode.Reject);

/// <summary>How an import behaves when a matching (org, major, minor, cu) version already exists.</summary>
public enum BaseAppImportMode
{
    /// <summary>Raise a validation error so a second ZIP can't silently overwrite.</summary>
    Reject = 0,
    /// <summary>Soft-delete the existing version, then create a fresh one with this ZIP's files.</summary>
    Replace = 1,
    /// <summary>Keep the existing version row; insert this ZIP's files into it. Same (version_id, path) overwrites.</summary>
    Append = 2,
}

/// <summary>
/// Counts and a small sample of unparseable file paths so the upload page can
/// show a useful confirmation. <c>FailedPaths</c> is capped at 20 entries.
/// <c>WasAppend</c> reports whether the upload landed on top of an existing
/// version (vs. creating a new one); <c>ReplacedPaths</c> is how many
/// per-path collisions overwrote an existing file row.
/// </summary>
public sealed record BaseAppImportSummary(
    int VersionId,
    int TotalFiles,
    int ParsedFiles,
    int FailedFiles,
    IReadOnlyList<string> FailedPaths,
    TimeSpan Duration,
    bool WasAppend,
    int ReplacedPaths);
