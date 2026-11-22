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
    private string _lastQuery = "";

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

        topPanel.Controls.Add(_queryBox);
        topPanel.Controls.Add(searchButton);
        topPanel.Controls.Add(clearButton);

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
        _results.Columns.Add("Name", 420);
        _results.Columns.Add("Size", 90);
        _results.Columns.Add("Files", 60);
        _results.Columns.Add("Seen", 60);
        _results.Columns.Add("First seen", 140);

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

        _service.TorrentIndexed += _ => RunSearchIfLive();

        _refreshTimer = new Timer { Interval = 4000 };
        _refreshTimer.Tick += (_, _) => RunSearchIfLive();
        _refreshTimer.Start();

        RunSearch();
    }

    private void RunSearchIfLive()
    {
        if (string.Equals(_queryBox.Text, _lastQuery, StringComparison.Ordinal))
            RunSearch();
    }

    private void RunSearch()
    {
        _lastQuery = _queryBox.Text;
        var results = _service.Index.Search(_queryBox.Text, 300);

        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var r in results)
        {
            var item = new ListViewItem(r.Name) { Tag = r };
            item.SubItems.Add(P2PShareService.FormatSize(r.TotalSize));
            item.SubItems.Add(r.FileCount.ToString());
            item.SubItems.Add(r.SeenCount.ToString());
            item.SubItems.Add(r.FirstSeenUtc.ToLocalTime().ToString("g"));
            _results.Items.Add(item);
        }
        _results.EndUpdate();

        var total = _service.Index.TotalIndexed();
        _statusLabel.Text = string.IsNullOrWhiteSpace(_queryBox.Text)
            ? $"{total} torrent(s) indexed so far from DHT traffic."
            : $"{results.Count} match(es) for \"{_queryBox.Text}\" out of {total} indexed.";
    }

    private SearchResult? SelectedResult() => _results.SelectedItem?.Tag as SearchResult;

    private void DownloadSelected()
    {
        var result = SelectedResult();
        if (result is null) return;
        _ = _service.Engine.StartDownloadAsync(result.Magnet);
        MessageBox.Show($"Added to Downloads:\n{result.Name}", "P2PShare");
    }

    private void CopyMagnetSelected()
    {
        var result = SelectedResult();
        if (result is null) return;
        try { Clipboard.SetText(result.Magnet); } catch { MessageBox.Show(result.Magnet, "Magnet link"); }
    }
}
