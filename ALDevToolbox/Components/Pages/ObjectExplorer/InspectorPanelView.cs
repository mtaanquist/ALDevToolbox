namespace ALDevToolbox.Components.Pages.ObjectExplorer;

/// <summary>
/// One entry in the file viewer's right-rail inspector stack. The stack starts
/// at <see cref="OutlinePanelView"/> for every freshly loaded file and grows
/// each time the user pushes a "Find references" gesture. The breadcrumb row
/// renders one chip per entry up to the cursor; clicking an earlier chip
/// truncates forward history (browser-style).
/// </summary>
public abstract record InspectorPanelView;

/// <summary>The default view — categorised list of the current file's symbols with a filter.</summary>
public sealed record OutlinePanelView : InspectorPanelView;

/// <summary>References for a specific symbol, fetched lazily on first render.</summary>
public sealed record ReferencesPanelView(long SymbolId, string SymbolName, string SymbolKind) : InspectorPanelView;

/// <summary>
/// In-file occurrences of a literal word. Backs the "Find in this file"
/// gesture — useful for variables, fields, and labels that <c>BaseAppSymbol</c>
/// doesn't index. Hits are computed lazily on the panel.
/// </summary>
public sealed record FileSearchPanelView(string Word) : InspectorPanelView;

/// <summary>
/// Mutable navigation history backing the inspector panel. Mirrors the
/// browser's back/forward stack: pushing always truncates any forward entries
/// past the current cursor, popping just moves the cursor without forgetting
/// later entries. Reset returns the stack to a fresh outline view — called
/// on every file load because references state is per-file.
/// </summary>
public sealed class InspectorPanelHistory
{
    private readonly List<InspectorPanelView> _stack = new() { new OutlinePanelView() };

    public IReadOnlyList<InspectorPanelView> Stack => _stack;
    public int Cursor { get; private set; }
    public InspectorPanelView Current => _stack[Cursor];

    public void Push(InspectorPanelView view)
    {
        if (Cursor < _stack.Count - 1)
        {
            _stack.RemoveRange(Cursor + 1, _stack.Count - Cursor - 1);
        }
        _stack.Add(view);
        Cursor = _stack.Count - 1;
    }

    public void PopTo(int index)
    {
        if (index < 0 || index >= _stack.Count) return;
        Cursor = index;
    }

    public void Reset()
    {
        _stack.Clear();
        _stack.Add(new OutlinePanelView());
        Cursor = 0;
    }
}
