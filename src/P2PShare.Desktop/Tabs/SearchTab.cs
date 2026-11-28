using System.Drawing;
using Majorsilence.Forms;
using P2PShare.Core;
using P2PShare.Core.Search;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop.Tabs;

public sealed class SearchTab : UserControl
{
    private readonly P2PShareService _service;
    private readonly TextBox _queryBox;
    private readonly ListView _results;
    private readonly Label _statusLabel;
    private readonly Timer _refreshTimer;
    private readonly ComboBox _category;
    private readonly CheckBox _hideAdult;
    private string _lastQuery = "";

    private const string AllCategories = "All categories";

    public SearchTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 48 };

        _queryBox = new TextBox
        {
            Left = 10,
            Top = 12,
            Width = 420,
            PlaceholderText = "Search the swarm (e.g. a title, a band name, a distro)...",
        };
        _queryBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) RunSearch(); };

        var searchButton = new Button { Left = 438, Top = 10, Width = 90, Height = 26, Text = "Search" };
        searchButton.Click += (_, _) => RunSearch();

        var clearButton = new Button { Left = 534, Top = 10, Width = 70, Height = 26, Text = "Clear" };
        clearButton.Click += (_, _) => { _queryBox.Text = ""; RunSearch(); };

        _category = new ComboBox { Left = 620, Top = 11, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _category.Items.Add(AllCategories);
        foreach (var name in Enum.GetNames<ContentCategory>()) _category.Items.Add(name);
        _category.SelectedIndex = 0;

        // Public DHT traffic includes plenty of adult material; leave it out unless asked for.
        _hideAdult = new CheckBox { Left = 782, Top = 12, Width = 110, Height = 24, Text = "Hide adult", Checked = true };

        topPanel.Controls.Add(_queryBox);
        topPanel.Controls.Add(searchButton);
        topPanel.Controls.Add(clearButton);
        topPanel.Controls.Add(_category);
        topPanel.Controls.Add(_hideAdult);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "The index fills in continuously as the DHT crawler observes real traffic - results appear here as they're found.",
        };

        _results = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _results.Columns.Add("Name", 380);
        _results.Columns.Add("Category", 80);
        _results.Columns.Add("Size", 85);
        _results.Columns.Add("Seeds", 60);
        _results.Columns.Add("Leechers", 70);
        _results.Columns.Add("Files", 50);
        _results.Columns.Add("Seen", 50);
        _results.Columns.Add("First seen", 130);

        var context = new ContextMenuStrip();
        var downloadItem = new ToolStripMenuItem("Download", null, (_, _) => DownloadSelected());
        var copyMagnetItem = new ToolStripMenuItem("Copy magnet link", null, (_, _) => CopyMagnetSelected());
        context.Items.Add(downloadItem);
        context.Items.Add(copyMagnetItem);
        _results.ContextMenuStrip = context;
        _results.ItemDoubleClicked += (_, _) => DownloadSelected();

        Controls.Add(_results);
        Controls.Add(_statusLabel);
        Controls.Add(topPanel);

        // New torrents are indexed on background threads. Rather than touching the list from those
        // threads (UI controls may only be used from the UI thread), the timer - which ticks on
        // the UI thread - picks up whatever has been added since the last tick.
        _refreshTimer = new Timer { Interval = 4000 };
        _refreshTimer.Tick += (_, _) => RunSearchIfLive();
        _refreshTimer.Start();

        // Hooked up only now, so that building the controls above doesn't run a half-made search.
        _category.SelectedIndexChanged += (_, _) => RunSearch();
        _hideAdult.CheckedChanged += (_, _) => RunSearch();

        RunSearch();
    }

    private void RunSearchIfLive()
    {
        // Don't refresh while the user is mid-edit: only re-run the query that is currently displayed.
        if (string.Equals(_queryBox.Text, _lastQuery, StringComparison.Ordinal))
            RunSearch();
    }

    private void RunSearch()
    {
        _lastQuery = _queryBox.Text;
        ContentCategory? category = Enum.TryParse<ContentCategory>(_category.SelectedItem?.ToString(), out var chosen) ? chosen : null;
        var results = _service.Index.Search(_queryBox.Text, 300, category, excludeAdult: _hideAdult.Checked);

        _results.Repopulate(results, r => r.InfoHashHex, r =>
        {
            var row = new ListViewItem(r.Name);
            row.SubItems.Add(r.Category.ToString());
            row.SubItems.Add(P2PShareService.FormatSize(r.TotalSize));
            // "-" = no tracker has answered yet; 0 = trackers know of nobody.
            row.SubItems.Add(r.Seeders?.ToString() ?? "-");
            row.SubItems.Add(r.Leechers?.ToString() ?? "-");
            row.SubItems.Add(r.FileCount.ToString());
            row.SubItems.Add(r.SeenCount.ToString());
            row.SubItems.Add(r.FirstSeenUtc.ToLocalTime().ToString("g"));
            return row;
        });

        var total = _service.Index.TotalIndexed();
        _statusLabel.Text = string.IsNullOrWhiteSpace(_queryBox.Text)
            ? $"{total} torrent(s) indexed so far from DHT traffic."
            : $"{results.Count} match(es) for \"{_queryBox.Text}\" out of {total} indexed.";
    }

    private SearchResult? SelectedResult() => _results.SelectedItem?.Tag as SearchResult;

    private async void DownloadSelected()
    {
        var result = SelectedResult();
        if (result is null) return;

        try
        {
            await _service.AddDownloadAsync(result.Magnet);
            MessageBox.Show($"Added to Downloads:\n{result.Name}", "P2PShare");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't start that download:\n{ex.Message}", "P2PShare");
        }
    }

    private void CopyMagnetSelected()
    {
        var result = SelectedResult();
        if (result is null) return;
        try { Clipboard.SetText(result.Magnet); } catch { MessageBox.Show(result.Magnet, "Magnet link"); }
    }
}
