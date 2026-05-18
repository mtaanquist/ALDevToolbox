namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Resolves the canonical link for the Object Explorer source-file viewer.
/// During the SSR-rewrite migration the new static-SSR viewer lives at
/// <c>/object-explorer/file/{id}</c> and the original Blazor InteractiveServer
/// viewer is kept at <c>/object-explorer/file-legacy/{id}</c>. Setting
/// the <c>OBJECT_EXPLORER_LEGACY_VIEWER</c> env var to <c>1</c> flips every
/// in-app link back to the legacy route for the duration of the rollout —
/// see <c>.design/source-viewer-redesign.md</c>.
/// </summary>
public sealed class ObjectExplorerLinks
{
    private readonly bool _useLegacy;

    public ObjectExplorerLinks()
    {
        _useLegacy = string.Equals(
            Environment.GetEnvironmentVariable("OBJECT_EXPLORER_LEGACY_VIEWER"),
            "1",
            StringComparison.Ordinal);
    }

    /// <summary>True when in-app links should target the legacy interactive viewer.</summary>
    public bool LegacyViewerActive => _useLegacy;

    /// <summary>Source-file viewer URL for the supplied file id.</summary>
    public string SourceFile(long fileId) =>
        _useLegacy
            ? $"/object-explorer/file-legacy/{fileId}"
            : $"/object-explorer/file/{fileId}";

    /// <summary>Source-file viewer URL with an initial line anchor.</summary>
    public string SourceFile(long fileId, int line) =>
        SourceFile(fileId) + $"?line={line}";

    /// <summary>Side-by-side file diff for a pair of <c>oe_module_files</c> ids.</summary>
    public string CompareFile(long leftFileId, long rightFileId) =>
        $"/object-explorer/compare/file?left={leftFileId}&right={rightFileId}";
}
