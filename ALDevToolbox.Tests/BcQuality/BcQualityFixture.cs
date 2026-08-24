using ALDevToolbox.Data;
using ALDevToolbox.Services.BcQuality;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.BcQuality;

/// <summary>
/// A throwaway BCQuality checkout on disk. The ingest tests build the tree
/// themselves rather than shipping fixture files, because half the contract
/// under test is what happens when the tree <em>changes</em> between runs
/// (update, prune, unchanged) — much easier to express by rewriting a file
/// than by carrying two fixture directories.
/// </summary>
internal sealed class BcQualityRepoFixture : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "aldt-bcq-tests", Guid.NewGuid().ToString("N"));

    public BcQualityRepoFixture() => Directory.CreateDirectory(Root);

    /// <summary>
    /// Writes one knowledge article in BCQuality's shape: six-field YAML
    /// frontmatter, a level-1 title, a required <c>## Description</c> section,
    /// and optional extra sections.
    /// </summary>
    public string WriteArticle(
        string relativePath,
        string title = "Nested grids are not supported",
        string description = "A grid nested inside another grid is not a supported pattern.",
        string bcVersion = "[all]",
        string domain = "ui",
        string keywords = "[grid, nested-grid, accessibility]",
        string technologies = "[al]",
        string countries = "[w1]",
        string applicationArea = "[all]",
        string? extraSections = null)
    {
        var body = $"""
            ---
            bc-version: {bcVersion}
            domain: {domain}
            keywords: {keywords}
            technologies: {technologies}
            countries: {countries}
            application-area: {applicationArea}
            ---

            # {title}

            ## Description

            {description}
            {extraSections ?? string.Empty}
            """;
        return WriteRaw(relativePath, body);
    }

    /// <summary>Writes a file verbatim — for the malformed cases the walker has to skip.</summary>
    public string WriteRaw(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a sibling sample file (<c>&lt;slug&gt;.&lt;kind&gt;.&lt;ext&gt;</c>) next to an article.</summary>
    public void WriteSample(string articleRelativePath, string kind, string extension, string content)
    {
        var slug = Path.GetFileNameWithoutExtension(articleRelativePath);
        var folder = Path.GetDirectoryName(articleRelativePath)!.Replace('\\', '/');
        WriteRaw($"{folder}/{slug}.{kind}.{extension}", content);
    }

    public void Delete(string relativePath) =>
        File.Delete(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* best effort — it's a temp directory */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }
}

internal static class BcQualityTestServices
{
    /// <summary>
    /// An ingest service wired for directory ingest. The process runner throws
    /// if anything reaches for git — the tests must never clone from the
    /// network, so a call would be a bug in the test, not a slow test.
    /// </summary>
    public static BcQualityIngestService NewIngestService(AppDbContext ctx, TimeProvider? clock = null) =>
        new(ctx, new RefusingProcessRunner(), clock ?? TimeProvider.System,
            NullLogger<BcQualityIngestService>.Instance);

    public static BcQualitySearchService NewSearchService(AppDbContext ctx) => new(ctx);

    private sealed class RefusingProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                $"A test tried to run '{request.FileName}'. Ingest tests must stay off the network.");
    }
}
