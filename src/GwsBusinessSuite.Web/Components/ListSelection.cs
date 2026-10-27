namespace GwsBusinessSuite.Web.Components;

// Row selection for a list's bulk actions (tech-forward T3): a click toggles one row, a
// shift-click selects everything between it and the last row clicked (in the list's current
// order), and select-all covers the rows on show. Rows that leave the list (filtered out,
// trashed) leave the selection with Retain.
public sealed class ListSelection<TKey> where TKey : notnull
{
    private readonly HashSet<TKey> _selected = [];
    private TKey? _anchor;
    private bool _hasAnchor;

    public int Count => _selected.Count;

    public IReadOnlyCollection<TKey> Items => _selected;

    public bool IsSelected(TKey key) => _selected.Contains(key);

    public bool AllSelected(IReadOnlyCollection<TKey> shown) => shown.Count > 0 && shown.All(_selected.Contains);

    public void Toggle(TKey key, bool range, IReadOnlyList<TKey> order)
    {
        var anchorAt = _hasAnchor ? IndexOf(order, _anchor!) : -1;
        var keyAt = IndexOf(order, key);
        if (range && anchorAt >= 0 && keyAt >= 0)
        {
            // The range takes the anchor's state: shift-clicking after selecting selects, after
            // clearing clears.
            var select = _selected.Contains(_anchor!);
            for (var at = Math.Min(anchorAt, keyAt); at <= Math.Max(anchorAt, keyAt); at++)
            {
                if (select) _selected.Add(order[at]); else _selected.Remove(order[at]);
            }
        }
        else if (!_selected.Remove(key))
        {
            _selected.Add(key);
        }
        _anchor = key;
        _hasAnchor = true;
    }

    public void SetAll(IEnumerable<TKey> shown, bool selected)
    {
        foreach (var key in shown)
        {
            if (selected) _selected.Add(key); else _selected.Remove(key);
        }
    }

    public void Clear()
    {
        _selected.Clear();
        _hasAnchor = false;
    }

    public void Retain(IEnumerable<TKey> existing) => _selected.IntersectWith(existing);

    private static int IndexOf(IReadOnlyList<TKey> order, TKey key)
    {
        for (var at = 0; at < order.Count; at++)
        {
            if (EqualityComparer<TKey>.Default.Equals(order[at], key)) return at;
        }
        return -1;
    }
}
