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

/// <summary>
/// One declaration the viewer should make clickable. Coordinates are 1-based
/// and align with what <c>AlSymbolExtractor</c> captures at import time.
///
/// <see cref="SymbolId"/> identifies the underlying row — its meaning depends
/// on <see cref="IsMemberSymbol"/>:
/// <list type="bullet">
///   <item><c>false</c> (object header): <c>oe_module_objects.Id</c>. The
///         right-click "Find references" routes via
///         <c>/from-symbol/{id}</c> (object-scoped query).</item>
///   <item><c>true</c> (procedure / field / trigger / event symbol):
///         <c>oe_module_symbols.Id</c>. The right-click "Find references"
///         routes via <c>/from-member-symbol/{id}</c> (member-scoped
///         query: declarations + calls + owner-type buckets).</item>
/// </list>
/// Two ID spaces don't mix — keeping the flag explicit lets the JS host
/// pick the right endpoint without having to guess from the kind string.
/// </summary>
public sealed record CodeViewerDeclaration(
    long SymbolId,
    int Line,
    int ColumnStart,
    int ColumnEnd,
    string Kind,
    string Name,
    bool IsMemberSymbol = false);

/// <summary>A 1-based click position inside the viewer's source.</summary>
public sealed record CodeViewerClick(int Line, int Column);

/// <summary>
/// One range the viewer should underline as resolvable. Same column convention
/// as <see cref="CodeViewerDeclaration"/>: 1-based, end-exclusive.
/// </summary>
public sealed record CodeViewerResolvable(int Line, int ColumnStart, int ColumnEnd);
