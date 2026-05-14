namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Resolved per-extension data carried through the generation pipeline.
/// Built by <see cref="GenerationService"/> from the loaded
/// <see cref="ALDevToolbox.Domain.Entities.RuntimeTemplate"/> and modules,
/// then handed to <see cref="WorkspaceZipBuilder"/> for ZIP emission.
/// </summary>
internal sealed record EmittableExtension(
    string Path,
    string Name,
    Guid Id,
    int IdRangeFrom,
    int IdRangeTo,
    string Application,
    string Runtime,
    string Publisher,
    bool IsModuleClone,
    string? ModuleKey,
    string ModuleName,
    IReadOnlyList<FolderNode> FolderRoots,
    IReadOnlyList<EmittableDependency> Dependencies);

/// <summary>Folder + its files + its children (recursive). Built once at load time.</summary>
internal sealed record FolderNode(
    string Path,
    IReadOnlyList<FileLeaf> Files,
    IReadOnlyList<FolderNode> Folders);

internal sealed record FileLeaf(string Path, string Content, bool IsExample);

internal sealed record EmittableDependency(
    string? RefExtensionPath,
    string? RefModuleKey,
    string? LitId,
    string? LitName,
    string? LitPublisher,
    string? LitVersion);
