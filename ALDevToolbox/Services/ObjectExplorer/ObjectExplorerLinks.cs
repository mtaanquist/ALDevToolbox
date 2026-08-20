namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Resolves the canonical link for the Object Explorer source-file viewer.
///
/// This used to choose between the static-SSR viewer and a copy of the
/// pre-#161 InteractiveServer one, kept at <c>/object-explorer/file-legacy/</c>
/// behind <c>OBJECT_EXPLORER_LEGACY_VIEWER=1</c> as a one-release rollback
/// path. Fifty-five releases went by without it being needed, so the second
/// viewer, the env var and the CSS they stranded were retired (#562) — an
/// untested bolt-hole is not a mitigation. There is one viewer now, and this
/// class is the one place its route is spelled.
/// </summary>
public sealed class ObjectExplorerLinks
{
    /// <summary>Source-file viewer URL for the supplied file id.</summary>
    public string SourceFile(long fileId) => $"/object-explorer/file/{fileId}";

    /// <summary>Source-file viewer URL with an initial line anchor.</summary>
    public string SourceFile(long fileId, int line) =>
        SourceFile(fileId) + $"?line={line}";

    /// <summary>
    /// Source-file viewer URL with a line anchor plus the Release the user is
    /// viewing <em>from</em> (<c>&amp;from=</c>). Carries the view-Release
    /// context onto a base object's source so a follow-up Find references stays
    /// seeded at the project Release. When <paramref name="fromReleaseId"/> is
    /// null this is identical to <see cref="SourceFile(long, int)"/>.
    /// </summary>
    public string SourceFile(long fileId, int line, int? fromReleaseId) =>
        fromReleaseId is { } from
            ? SourceFile(fileId, line) + $"&from={from}"
            : SourceFile(fileId, line);

    /// <summary>Side-by-side file diff for a pair of <c>oe_module_files</c> ids.</summary>
    public string CompareFile(long leftFileId, long rightFileId) =>
        $"/object-explorer/compare/file?left={leftFileId}&right={rightFileId}";

    /// <summary>
    /// The Release page's Compare scope, already pointed at the second
    /// release - the full, paged change list a file diff's change rail is a
    /// capped slice of.
    /// </summary>
    public string ReleaseCompare(int leftReleaseId, int rightReleaseId) =>
        $"/object-explorer/release/{leftReleaseId}?scope=Compare&right={rightReleaseId}";
}
