using System.Collections;
using System.ComponentModel;
using System.Windows.Data;

namespace LiteHibernate.UI;

internal sealed class ApplicationListOrder
{
    private readonly ListCollectionView view;
    private SortDescription[] sorts = [];
    public bool IsPinned { get; private set; }

    public ApplicationListOrder(ListCollectionView view)
    {
        this.view = view;
        view.LiveSortingProperties.Add(nameof(AppRow.RamBytes));
        view.LiveSortingProperties.Add(nameof(AppRow.Name));
        view.IsLiveSorting = true;
    }
    public void SetPinned(bool value)
    {
        if (IsPinned == value) return;
        if (value) CaptureOrder();
        else RestoreSort(sorts, live: true);
        IsPinned = value;
    }
    public void SortPinned(string property, ListSortDirection direction, bool append)
    {
        if (!IsPinned) return;
        var next = append ? sorts.ToList() : [];
        var index = next.FindIndex(sort => sort.PropertyName == property);
        if (index >= 0) next[index] = new(property, direction);
        else next.Add(new(property, direction));
        RestoreSort(next, live: false);
        CaptureOrder();
    }
    private void CaptureOrder()
    {
        // Include filtered-out rows so changing the search does not discard their position.
        var order = view.Cast<AppRow>().Concat(view.SourceCollection.Cast<AppRow>()).Distinct()
            .Select((row, index) => (row, index)).ToDictionary(entry => entry.row, entry => entry.index);
        sorts = view.SortDescriptions.ToArray();
        view.IsLiveSorting = false;
        view.CustomSort = new PinnedComparer(order);
    }
    private void RestoreSort(IEnumerable<SortDescription> descriptions, bool live)
    {
        using (view.DeferRefresh())
        {
            view.CustomSort = null;
            view.SortDescriptions.Clear();
            foreach (var sort in descriptions) view.SortDescriptions.Add(sort);
            view.IsLiveSorting = live;
        }
    }
    private sealed class PinnedComparer(Dictionary<AppRow, int> order) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            var left = (AppRow)x!; var right = (AppRow)y!;
            var result = order.GetValueOrDefault(left, int.MaxValue).CompareTo(order.GetValueOrDefault(right, int.MaxValue));
            // Newly discovered rows go after the existing rows, in a stable order.
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Rule.Id, right.Rule.Id);
        }
    }
}
