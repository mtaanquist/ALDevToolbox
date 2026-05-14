using Microsoft.JSInterop;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Minimal target for the CodeMirror "Find references" right-click menu's
/// <c>DotNetObjectReference.invokeMethodAsync</c> call. Holds a back-pointer
/// to the owning <see cref="CodeViewer"/> so the gesture can route through
/// the parameterised <c>OnFindReferences</c> callback the page wired up.
/// </summary>
public sealed class CodeViewerCallbacks
{
    private readonly CodeViewer _owner;

    public CodeViewerCallbacks(CodeViewer owner)
    {
        _owner = owner;
    }

    [JSInvokable]
    public Task OnFindReferences(long symbolId) => _owner.TriggerFindReferencesAsync(symbolId);
}

/// <summary>
/// One declaration the viewer should make clickable. Coordinates are 1-based
/// and align with what <c>AlSymbolExtractor</c> captures at import time.
/// </summary>
public sealed record CodeViewerDeclaration(
    long SymbolId,
    int Line,
    int ColumnStart,
    int ColumnEnd,
    string Kind,
    string Name);
