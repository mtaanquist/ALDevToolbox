namespace ALDevToolbox.Components.Shared;

/// <summary>
/// The ticked rows of a list whose search and filters can take some of them off the
/// screen. Searching, filtering and changing views never untick anything: the upgrade
/// team finds eight customers one short name at a time, and a selection that forgot the
/// first three on the fourth search is one nobody could build (#985). Only a re-read of
/// the data removes a tick, for a row that is gone or can no longer be acted on
/// (<see cref="Prune"/>).
///
/// <para>Because part of the selection can be off screen, the page says so: the count
/// names how many of the ticks are on screen (<see cref="Summary"/>), and
/// <see cref="ShowSelectedOnly"/> lets the reader bring every ticked row back into view.
/// The header checkbox keeps meaning "every row shown" (<see cref="ToggleShown"/>) and
/// never touches a tick that is off screen.</para>
///
/// <para>Holds environment ids today, but knows nothing of environments: the rows are
/// the page's, and each call hands in the ids it is talking about. Shared by the
/// Environments list and the Upgrades page; see .design/environment-updates.md.</para>
/// </summary>
public sealed class SelectionSet
{
    private readonly HashSet<int> _ids = new();
    private bool _showSelectedOnly;
    private object? _filtersWhenShown;

    /// <summary>How many rows are ticked, on screen or not.</summary>
    public int Count => _ids.Count;

    /// <summary>The ticked ids, on screen or not.</summary>
    public IReadOnlyCollection<int> Ids => _ids;

    /// <summary>
    /// True while the table shows the ticked rows and nothing else. It ends when the
    /// last tick goes: a table filtered to nothing, behind a toggle whose bar has gone,
    /// would be a page with no way back.
    /// </summary>
    public bool ShowSelectedOnly => _showSelectedOnly && _ids.Count > 0;

    /// <summary>
    /// Switches the ticked-only view on or off. <paramref name="filters"/> is the page's
    /// search and filters as they stand (a value tuple compares by value); the view holds
    /// only for as long as they do - see <see cref="Shown{T}"/>.
    /// </summary>
    public void ShowSelected(bool on, object? filters)
    {
        _showSelectedOnly = on;
        _filtersWhenShown = filters;
    }

    public bool Contains(int id) => _ids.Contains(id);

    /// <summary>Ticks or unticks one row.</summary>
    public void Set(int id, bool on)
    {
        if (on) _ids.Add(id);
        else if (_ids.Remove(id) && _ids.Count == 0) _showSelectedOnly = false;
    }

    /// <summary>Unticks everything, on screen or not, and leaves the ticked-only view.</summary>
    public void Clear()
    {
        _ids.Clear();
        _showSelectedOnly = false;
    }

    /// <summary>
    /// The header checkbox: ticks every row shown, or unticks every row shown. A tick
    /// on a row that is off screen stays as it was either way - the box is labelled for
    /// the rows on screen, and it must not reach past them.
    /// </summary>
    public void ToggleShown(IEnumerable<int> shownIds, bool on)
    {
        foreach (var id in shownIds) Set(id, on);
    }

    /// <summary>Every row shown is ticked (and at least one row is shown).</summary>
    public bool AllShownPicked(IEnumerable<int> shownIds)
    {
        var any = false;
        foreach (var id in shownIds)
        {
            if (!_ids.Contains(id)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Some of the rows shown are ticked but not all: the header box shows a dash. Ticks
    /// off screen don't count, because the box is about the rows under it.
    /// </summary>
    public bool SomeShownPicked(IEnumerable<int> shownIds)
    {
        var list = shownIds as IReadOnlyCollection<int> ?? shownIds.ToList();
        return list.Any(_ids.Contains) && !AllShownPicked(list);
    }

    /// <summary>How many of the ticked rows are among the ones shown.</summary>
    public int ShownCount(IEnumerable<int> shownIds) => shownIds.Distinct().Count(_ids.Contains);

    /// <summary>
    /// "8 selected", or "8 selected, 5 shown" when some ticks are off screen. The second
    /// half only appears when it says something: "8 selected, 8 shown" is noise.
    /// </summary>
    public string Summary(IEnumerable<int> shownIds)
    {
        var shown = ShownCount(shownIds);
        return shown == _ids.Count ? $"{_ids.Count} selected" : $"{_ids.Count} selected, {shown} shown";
    }

    /// <summary>
    /// Drops the ticks on rows that no longer exist or can no longer be acted on. Called
    /// after the data is read again, never after a search or a filter change.
    /// </summary>
    public void Prune(IEnumerable<int> existingIds)
    {
        if (_ids.Count == 0) return;
        var existing = existingIds as ISet<int> ?? existingIds.ToHashSet();
        _ids.RemoveWhere(id => !existing.Contains(id));
        if (_ids.Count == 0) _showSelectedOnly = false;
    }

    /// <summary>
    /// The rows the table shows. With <see cref="ShowSelectedOnly"/> on, that is every
    /// ticked row out of <paramref name="all"/>, whatever the search and filters say:
    /// the reader asked to see the selection, and a search left in the box from finding
    /// the last customer must not hide the other seven. Changing the search or a filter
    /// afterwards is asking for something else, so it ends the ticked-only view and the
    /// table goes back to <paramref name="filtered"/> - with every tick kept.
    /// </summary>
    /// <param name="filters">The page's search and filters as they stand now, compared with the ones the view was switched on under.</param>
    public IEnumerable<T> Shown<T>(IEnumerable<T> filtered, IEnumerable<T> all, Func<T, int> id, object? filters)
    {
        if (_showSelectedOnly && !Equals(filters, _filtersWhenShown)) _showSelectedOnly = false;
        return ShowSelectedOnly ? all.Where(r => _ids.Contains(id(r))) : filtered;
    }
}
