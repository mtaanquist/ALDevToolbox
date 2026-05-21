using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Read- and write-side service for <see cref="Snippet"/>s. Reads back the
/// browser pages at <c>/snippets</c> and the admin CRUD pages at
/// <c>/admin/snippets</c>; writes validate via field-keyed
/// <see cref="PlanValidationException"/> so the form can render errors inline.
/// Search uses Postgres <c>ILIKE</c> with the trigram index added by the
/// <c>AddSnippets</c> migration.
/// </summary>
public class SnippetService
{
    /// <summary>Cap on per-file content size so a runaway paste can't blow up the DB row.</summary>
    public const int MaxFileContentLength = 100_000;
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxKeywordsLength = 500;
    public const int MaxFileNameLength = 260;
    /// <summary>
    /// Generous Markdown body cap. Big enough for multi-section setup notes
    /// with code fences; small enough that a runaway paste can't bloat the
    /// row (a single snippet would have to be quite pathological to top this).
    /// </summary>
    public const int MaxInstructionsLength = 10_000;

    private readonly AppDbContext _db;
    private readonly ILogger<SnippetService> _logger;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;

    public SnippetService(AppDbContext db, ILogger<SnippetService> logger, IOrganizationContext orgContext, StorageQuotaGuard quotaGuard)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns snippets matching the fuzzy <paramref name="query"/> over title,
    /// description, and keywords (case-insensitive, substring). An empty query
    /// returns every active snippet. Soft-deleted rows are always excluded;
    /// deprecated rows are excluded unless <paramref name="includeDeprecated"/> is set.
    /// </summary>
    public async Task<List<Snippet>> SearchAsync(string? query, bool includeDeprecated = false, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        var rows = _db.Snippets
            .AsNoTracking()
            .Include(s => s.MinimumApplicationVersion)
            .Where(s => s.DeletedAt == null);

        if (!includeDeprecated)
        {
            rows = rows.Where(s => !s.Deprecated);
        }

        if (trimmed.Length > 0)
        {
            var pattern = "%" + trimmed + "%";
            rows = rows.Where(s =>
                EF.Functions.ILike(s.Title, pattern)
                || EF.Functions.ILike(s.Description, pattern)
                || EF.Functions.ILike(s.Keywords, pattern));
        }

        return await rows
            .OrderBy(s => s.Title)
            .ToListAsync(ct);
    }

    /// <summary>Returns every snippet (optionally including soft-deleted), with files. Drives the admin list.</summary>
    public Task<List<Snippet>> GetAllForAdminAsync(bool includeDeleted, CancellationToken ct = default)
    {
        var query = _db.Snippets.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(s => s.DeletedAt == null);
        }

        return query
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.MinimumApplicationVersion)
            .OrderBy(s => s.DeletedAt == null ? 0 : 1)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
    }

    /// <summary>Returns a single snippet with its files. <c>null</c> when not found in this org.</summary>
    public Task<Snippet?> GetAsync(int id, CancellationToken ct = default)
    {
        return _db.Snippets
            .AsNoTracking()
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.MinimumApplicationVersion)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>Creates a snippet plus its files. Throws <see cref="PlanValidationException"/> on validation failure.</summary>
    public async Task<Snippet> CreateAsync(SnippetInput input, CancellationToken ct = default)
    {
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        await ValidateAsync(input, existingId: null, orgId, ct);

        var now = DateTime.UtcNow;
        var snippet = new Snippet
        {
            OrganizationId = orgId,
            Title = input.Title.Trim(),
            Description = input.Description.Trim(),
            Keywords = NormaliseKeywords(input.Keywords),
            Deprecated = input.Deprecated,
            Instructions = NullIfBlank(input.Instructions),
            MinimumApplicationVersionId = input.MinimumApplicationVersionId,
            CreatedAt = now,
            UpdatedAt = now,
            Files = input.Files
                .Select((f, i) => new SnippetFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    FileName = f.FileName.Trim(),
                    Content = f.Content ?? string.Empty,
                })
                .ToList(),
        };

        _db.Snippets.Add(snippet);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created snippet '{Title}' (id={Id}) with {FileCount} file(s).",
            snippet.Title, snippet.Id, snippet.Files.Count);
        return snippet;
    }

    /// <summary>Updates a snippet's fields and reconciles its file list.</summary>
    public async Task UpdateAsync(int id, SnippetInput input, CancellationToken ct = default)
    {
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        var existing = await _db.Snippets
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Snippet with id {id} was not found.",
            });

        await ValidateAsync(input, existingId: id, orgId, ct);

        existing.Title = input.Title.Trim();
        existing.Description = input.Description.Trim();
        existing.Keywords = NormaliseKeywords(input.Keywords);
        existing.Deprecated = input.Deprecated;
        existing.Instructions = NullIfBlank(input.Instructions);
        existing.MinimumApplicationVersionId = input.MinimumApplicationVersionId;
        existing.UpdatedAt = DateTime.UtcNow;

        ReconcileFiles(existing, input.Files, orgId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated snippet '{Title}' (id={Id}); now has {FileCount} file(s).",
            existing.Title, existing.Id, existing.Files.Count);
    }

    /// <summary>Soft-deletes a snippet by stamping <see cref="Snippet.DeletedAt"/>.</summary>
    public async Task SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Snippets.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Snippet with id {id} was not found.",
            });

        if (existing.DeletedAt is not null) return;

        existing.DeletedAt = DateTime.UtcNow;
        existing.UpdatedAt = existing.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Soft-deleted snippet '{Title}' (id={Id}).", existing.Title, existing.Id);
    }

    /// <summary>Clears <see cref="Snippet.DeletedAt"/> on a previously soft-deleted snippet.</summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Snippets.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Snippet with id {id} was not found.",
            });

        if (existing.DeletedAt is null) return;

        existing.DeletedAt = null;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Restored snippet '{Title}' (id={Id}).", existing.Title, existing.Id);
    }

    /// <summary>Flips the <see cref="Snippet.Deprecated"/> flag.</summary>
    public async Task SetDeprecatedAsync(int id, bool deprecated, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var existing = await _db.Snippets.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Snippet with id {id} was not found.",
            });

        if (existing.Deprecated == deprecated) return;

        existing.Deprecated = deprecated;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Set snippet '{Title}' (id={Id}) deprecated={Deprecated}.",
            existing.Title, existing.Id, deprecated);
    }

    /// <summary>Lower-cases and collapses internal whitespace so search is consistent across submissions.</summary>
    internal static string NormaliseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = raw.Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens.Select(t => t.ToLowerInvariant()));
    }

    /// <summary>Trims to null for whitespace-only input so an empty textarea persists as null rather than "".</summary>
    internal static string? NullIfBlank(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private async Task ValidateAsync(SnippetInput input, int? existingId, int orgId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var title = input.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            errors[nameof(input.Title)] = "Title is required.";
        }
        else if (title.Length > MaxTitleLength)
        {
            errors[nameof(input.Title)] = $"Title must be {MaxTitleLength} characters or fewer.";
        }
        else
        {
            var existingTitleOwner = await _db.Snippets
                .AsNoTracking()
                .Where(s => s.OrganizationId == orgId && s.Title == title && s.DeletedAt == null)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (existingTitleOwner is not null && existingTitleOwner != existingId)
            {
                errors[nameof(input.Title)] = $"A snippet with title '{title}' already exists.";
            }
        }

        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(description))
        {
            errors[nameof(input.Description)] = "Description is required.";
        }
        else if (description.Length > MaxDescriptionLength)
        {
            errors[nameof(input.Description)] = $"Description must be {MaxDescriptionLength} characters or fewer.";
        }

        if ((input.Keywords ?? string.Empty).Length > MaxKeywordsLength)
        {
            errors[nameof(input.Keywords)] = $"Keywords must be {MaxKeywordsLength} characters or fewer.";
        }

        await ValidateMetadataAsync(_db, input.Instructions, input.MinimumApplicationVersionId, errors, ct);

        ValidateFiles(input.Files, errors);

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }

    /// <summary>
    /// Validates the optional Markdown instructions length and that the picked
    /// <see cref="ApplicationVersion"/> exists and isn't soft-deleted. Shared
    /// with <see cref="SnippetSuggestionService"/> so both surfaces enforce
    /// the same rules.
    /// </summary>
    internal static async Task ValidateMetadataAsync(
        AppDbContext db,
        string? instructions,
        int? minimumApplicationVersionId,
        IDictionary<string, string> errors,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(instructions) && instructions.Length > MaxInstructionsLength)
        {
            errors["Instructions"] = $"Instructions must be {MaxInstructionsLength} characters or fewer.";
        }

        if (minimumApplicationVersionId is int versionId)
        {
            // Allow rows the EF filter would normally hide (other orgs are
            // impossible here because the catalogue is org-scoped, but a row
            // that was soft-deleted between page load and save would
            // otherwise produce an opaque FK error). Soft-deleted rows are
            // refused with a friendly message; deprecated rows are accepted
            // so an existing snippet stays valid after a catalogue cleanup.
            var row = await db.ApplicationVersions
                .AsNoTracking()
                .Where(a => a.Id == versionId)
                .Select(a => new { a.DeletedAt })
                .FirstOrDefaultAsync(ct);
            if (row is null)
            {
                errors["MinimumApplicationVersionId"] = "Selected application version no longer exists.";
            }
            else if (row.DeletedAt is not null)
            {
                errors["MinimumApplicationVersionId"] = "Selected application version has been removed from the catalogue.";
            }
        }
    }

    /// <summary>
    /// Shared file-list validation reused by <see cref="SnippetService"/> and
    /// <see cref="SnippetSuggestionService"/> so both surfaces enforce the same
    /// rules on file names, content size and duplicates.
    /// </summary>
    internal static void ValidateFiles(IReadOnlyList<SnippetFileInput> files, IDictionary<string, string> errors)
    {
        if (files.Count == 0)
        {
            errors["Files"] = "At least one file is required.";
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = file.FileName?.Trim() ?? string.Empty;
            var fileFieldKey = $"Files[{i}].FileName";
            var contentFieldKey = $"Files[{i}].Content";

            if (string.IsNullOrEmpty(name))
            {
                errors[fileFieldKey] = "File name is required.";
            }
            else if (name.Length > MaxFileNameLength)
            {
                errors[fileFieldKey] = $"File name must be {MaxFileNameLength} characters or fewer.";
            }
            else if (name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Any(char.IsControl))
            {
                errors[fileFieldKey] = "File name must be a flat file name — no slashes, no '..', no control characters.";
            }
            else if (!seen.Add(name))
            {
                errors[fileFieldKey] = $"Duplicate file name '{name}' (case-insensitive).";
            }

            var content = file.Content ?? string.Empty;
            if (content.Length > MaxFileContentLength)
            {
                errors[contentFieldKey] = $"File content must be {MaxFileContentLength} characters or fewer.";
            }
        }
    }

    private static void ReconcileFiles(Snippet existing, IReadOnlyList<SnippetFileInput> inputs, int orgId)
    {
        var existingFiles = existing.Files.OrderBy(f => f.Ordering).ToList();

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var name = input.FileName.Trim();
            var content = input.Content ?? string.Empty;

            if (i < existingFiles.Count)
            {
                var file = existingFiles[i];
                file.Ordering = i;
                file.FileName = name;
                file.Content = content;
            }
            else
            {
                existing.Files.Add(new SnippetFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    FileName = name,
                    Content = content,
                });
            }
        }

        for (var i = inputs.Count; i < existingFiles.Count; i++)
        {
            existing.Files.Remove(existingFiles[i]);
        }
    }
}

/// <summary>Form-shaped admin input for snippet create/update.</summary>
public record SnippetInput(
    string Title,
    string Description,
    string Keywords,
    bool Deprecated,
    IReadOnlyList<SnippetFileInput> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null);

/// <summary>One file row submitted by the snippet file editor.</summary>
public record SnippetFileInput(string FileName, string Content);
