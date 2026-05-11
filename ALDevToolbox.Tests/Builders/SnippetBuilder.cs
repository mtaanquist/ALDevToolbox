using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Tests.Builders;

/// <summary>
/// Constructs <see cref="Snippet"/> rows pre-populated with sensible defaults.
/// Keep this small — fluent extensions only, no canned variants beyond what
/// multiple tests share.
/// </summary>
public static class SnippetBuilder
{
    public const int DefaultOrganizationId = 1;

    public static Snippet Default(string title = "Test Snippet", int organizationId = DefaultOrganizationId) => new()
    {
        OrganizationId = organizationId,
        Title = title,
        Description = "Synthetic snippet used in tests.",
        Keywords = "test sample",
        Deprecated = false,
        CreatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc),
    };

    public static Snippet WithFile(this Snippet snippet, string fileName, string content)
    {
        snippet.Files.Add(new SnippetFile
        {
            OrganizationId = snippet.OrganizationId,
            Ordering = snippet.Files.Count,
            FileName = fileName,
            Content = content,
        });
        return snippet;
    }
}
