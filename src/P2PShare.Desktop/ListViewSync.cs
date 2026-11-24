using Majorsilence.Forms;

namespace P2PShare.Desktop;

internal static class ListViewSync
{
    /// <summary>
    /// Replaces a list's rows with <paramref name="items"/> and re-selects the row that was selected
    /// before (matched by <paramref name="keyOf"/>). The tabs refresh on a timer, and clearing the
    /// list also clears the selection - without this, a button like Pause or Remove usually finds
    /// nothing selected, because the refresh ran between the click on the row and the click on the button.
    /// </summary>
    public static void Repopulate<T>(this ListView list, IEnumerable<T> items, Func<T, string> keyOf, Func<T, ListViewItem> makeRow)
    {
        var selectedKey = list.SelectedItem?.Tag is T selected ? keyOf(selected) : null;

        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var item in items)
            {
                var row = makeRow(item);
                row.Tag = item;
                list.Items.Add(row);
                if (selectedKey is not null && string.Equals(keyOf(item), selectedKey, StringComparison.OrdinalIgnoreCase))
                    row.Selected = true;
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }
}
