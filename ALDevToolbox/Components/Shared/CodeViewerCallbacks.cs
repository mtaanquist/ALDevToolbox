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

    [JSInvokable]
    public Task OnGoToDefinition(int line, int column) =>
        _owner.TriggerGoToDefinitionAsync(line, column);

    [JSInvokable]
    public Task OnFindInFile(int line, int column) =>
        _owner.TriggerFindInFileAsync(line, column);
}

/// <summary>A 1-based click position inside the viewer's source.</summary>
public sealed record CodeViewerClick(int Line, int Column);
