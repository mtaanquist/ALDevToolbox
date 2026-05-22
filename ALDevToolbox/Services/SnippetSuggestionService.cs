using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// User-submitted snippet suggestions and the admin approval queue. Any
/// signed-in user can <see cref="SubmitAsync"/>; admins
/// <see cref="ApproveAsync"/> (promoting the draft to a real
/// <see cref="Snippet"/>) or <see cref="RejectAsync"/> from the queue at
/// <c>/admin/snippets/suggestions</c>.
/// </summary>
public class SnippetSuggestionService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SnippetSuggestionService> _logger;
    private readonly IOrganizationContext _orgContext;

    public SnippetSuggestionService(AppDbContext db, ILogger<SnippetSuggestionService> logger, IOrganizationContext orgContext)
    {
        _db = db;
        _logger = logger;
        _orgContext = orgContext;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; service mutation called outside an authenticated request.");

    /// <summary>Submits a draft snippet for admin review. Returns the persisted row's id.</summary>
    public async Task<int> SubmitAsync(SnippetSuggestionInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();

        await ValidateAsync(input, ct);

        var now = DateTime.UtcNow;
        var suggestion = new SnippetSuggestion
        {
            OrganizationId = orgId,
            SuggestedByUserId = userId,
            Title = input.Title.Trim(),
            Description = input.Description.Trim(),
            Keywords = SnippetService.NormaliseKeywords(input.Keywords),
            Instructions = SnippetService.NullIfBlank(input.Instructions),
            MinimumApplicationVersionId = input.MinimumApplicationVersionId,
            Decision = SnippetSuggestionDecision.Pending,
            RequestedAt = now,
            Files = input.Files
                .Select((f, i) => new SnippetSuggestionFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    FileName = f.FileName.Trim(),
                    Content = f.Content ?? string.Empty,
                })
                .ToList(),
        };

        _db.SnippetSuggestions.Add(suggestion);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} submitted snippet suggestion '{Title}' (id={Id}) with {FileCount} file(s).",
            userId, suggestion.Title, suggestion.Id, suggestion.Files.Count);
        return suggestion.Id;
    }

    /// <summary>Returns pending suggestions (with files) for the admin queue, oldest first.</summary>
    public Task<List<SnippetSuggestion>> GetPendingAsync(CancellationToken ct = default)
    {
        return _db.SnippetSuggestions
            .AsNoTracking()
            .Where(s => s.Decision == SnippetSuggestionDecision.Pending)
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.SuggestedByUser)
            .Include(s => s.MinimumApplicationVersion)
            .OrderBy(s => s.RequestedAt)
            .ToListAsync(ct);
    }

    /// <summary>Returns one suggestion (with files) for the detail view.</summary>
    public Task<SnippetSuggestion?> GetAsync(int id, CancellationToken ct = default)
    {
        return _db.SnippetSuggestions
            .AsNoTracking()
            .Include(s => s.Files.OrderBy(f => f.Ordering))
            .Include(s => s.SuggestedByUser)
            .Include(s => s.MinimumApplicationVersion)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Updates a pending suggestion in place. Only the user who originally
    /// submitted it can edit; approved or rejected suggestions are
    /// terminal and refuse the update. Reconciles the file list against
    /// the new input by position (existing rows are reused, extras are
    /// added, surplus rows are removed) so the audit trail attributes
    /// the change to the file rows rather than recording delete+create.
    /// </summary>
    public async Task UpdateAsync(int suggestionId, SnippetSuggestionInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = RequireUserId();

        var existing = await _db.SnippetSuggestions
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (existing.Decision != SnippetSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {existing.Decision.ToString().ToLowerInvariant()} and can no longer be edited.",
            });
        }

        if (existing.SuggestedByUserId != userId)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["SuggestedByUserId"] = "Only the user who submitted a suggestion can edit it.",
            });
        }

        await ValidateAsync(input, ct);

        existing.Title = input.Title.Trim();
        existing.Description = input.Description.Trim();
        existing.Keywords = SnippetService.NormaliseKeywords(input.Keywords);
        existing.Instructions = SnippetService.NullIfBlank(input.Instructions);
        existing.MinimumApplicationVersionId = input.MinimumApplicationVersionId;

        ReconcileFiles(existing, input.Files, orgId);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} updated snippet suggestion '{Title}' (id={Id}); now has {FileCount} file(s).",
            userId, existing.Title, existing.Id, existing.Files.Count);
    }

    /// <summary>
    /// Promotes a pending suggestion to a real <see cref="Snippet"/>. Runs
    /// inside a transaction so the snippet, its files, and the suggestion's
    /// decision columns either all land or none do.
    /// </summary>
    public async Task<Snippet> ApproveAsync(int suggestionId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var deciderId = RequireUserId();

        var suggestion = await _db.SnippetSuggestions
            .Include(s => s.Files)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (suggestion.Decision != SnippetSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {suggestion.Decision.ToString().ToLowerInvariant()}.",
            });
        }

        // Title-uniqueness within the org is the only reason approval can
        // fail outside the original validation pass — another suggestion
        // could have been approved with the same title in between.
        var titleClash = await _db.Snippets
            .AsNoTracking()
            .AnyAsync(s => s.OrganizationId == orgId
                           && s.Title == suggestion.Title
                           && s.DeletedAt == null, ct);
        if (titleClash)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Title"] = $"A snippet with title '{suggestion.Title}' already exists. Reject this suggestion or edit the existing snippet.",
            });
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var snippet = new Snippet
        {
            OrganizationId = orgId,
            Title = suggestion.Title,
            Description = suggestion.Description,
            Keywords = suggestion.Keywords,
            Deprecated = false,
            Instructions = suggestion.Instructions,
            MinimumApplicationVersionId = suggestion.MinimumApplicationVersionId,
            CreatedAt = now,
            UpdatedAt = now,
            Files = suggestion.Files
                .OrderBy(f => f.Ordering)
                .Select((f, i) => new SnippetFile
                {
                    OrganizationId = orgId,
                    Ordering = i,
                    FileName = f.FileName,
                    Content = f.Content,
                })
                .ToList(),
        };

        _db.Snippets.Add(snippet);
        await _db.SaveChangesAsync(ct);

        suggestion.Decision = SnippetSuggestionDecision.Approved;
        suggestion.DecidedAt = now;
        suggestion.DecidedByUserId = deciderId;
        suggestion.ApprovedSnippetId = snippet.Id;
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Approved snippet suggestion '{Title}' (id={SuggestionId}) → snippet id={SnippetId}.",
            suggestion.Title, suggestion.Id, snippet.Id);
        return snippet;
    }

    /// <summary>Rejects a pending suggestion. Optional <paramref name="note"/> is recorded for the audit trail.</summary>
    public async Task RejectAsync(int suggestionId, string? note, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var deciderId = RequireUserId();

        var suggestion = await _db.SnippetSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Id"] = $"Suggestion with id {suggestionId} was not found.",
            });

        if (suggestion.Decision != SnippetSuggestionDecision.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Decision"] = $"Suggestion is already {suggestion.Decision.ToString().ToLowerInvariant()}.",
            });
        }

        suggestion.Decision = SnippetSuggestionDecision.Rejected;
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = deciderId;
        var trimmedNote = note?.Trim();
        suggestion.DecisionNote = string.IsNullOrEmpty(trimmedNote) ? null : trimmedNote;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Rejected snippet suggestion '{Title}' (id={Id}).",
            suggestion.Title, suggestion.Id);
    }

    private async Task ValidateAsync(SnippetSuggestionInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        var title = input.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            errors[nameof(input.Title)] = "Title is required.";
        }
        else if (title.Length > SnippetService.MaxTitleLength)
        {
            errors[nameof(input.Title)] = $"Title must be {SnippetService.MaxTitleLength} characters or fewer.";
        }

        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(description))
        {
            errors[nameof(input.Description)] = "Description is required.";
        }
        else if (description.Length > SnippetService.MaxDescriptionLength)
        {
            errors[nameof(input.Description)] = $"Description must be {SnippetService.MaxDescriptionLength} characters or fewer.";
        }

        if ((input.Keywords ?? string.Empty).Length > SnippetService.MaxKeywordsLength)
        {
            errors[nameof(input.Keywords)] = $"Keywords must be {SnippetService.MaxKeywordsLength} characters or fewer.";
        }

        await SnippetService.ValidateMetadataAsync(
            _db, input.Instructions, input.MinimumApplicationVersionId, errors, ct);

        SnippetService.ValidateFiles(input.Files, errors);

        if (errors.Count > 0)
        {
            throw new PlanValidationException(errors);
        }
    }

    private static void ReconcileFiles(SnippetSuggestion existing, IReadOnlyList<SnippetFileInput> inputs, int orgId)
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
                existing.Files.Add(new SnippetSuggestionFile
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

/// <summary>Form-shaped input from <c>/snippets/suggest</c>.</summary>
public record SnippetSuggestionInput(
    string Title,
    string Description,
    string Keywords,
    IReadOnlyList<SnippetFileInput> Files,
    string? Instructions = null,
    int? MinimumApplicationVersionId = null);
