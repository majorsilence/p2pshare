using Majorsilence.Forms;
using MonoTorrent.Client;
using P2PShare.Core;
using P2PShare.Core.Engine;
using P2PShare.Desktop.Dialogs;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop.Tabs;

public sealed class DownloadsTab : UserControl
{
    private readonly P2PShareService _service;
    private readonly ListView _list;
    private readonly Timer _refreshTimer;

    public DownloadsTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44 };
        var addMagnet = new Button { Left = 10, Top = 8, Width = 130, Height = 28, Text = "Add Magnet..." };
        addMagnet.Click += (_, _) => AddMagnet();
        var addTorrent = new Button { Left = 148, Top = 8, Width = 140, Height = 28, Text = "Add .torrent..." };
        addTorrent.Click += (_, _) => AddTorrentFile();
        var pause = new Button { Left = 298, Top = 8, Width = 80, Height = 28, Text = "Pause" };
        pause.Click += (_, _) => WithSelected(h => _service.Engine.PauseAsync(h));
        var resume = new Button { Left = 382, Top = 8, Width = 80, Height = 28, Text = "Resume" };
        resume.Click += (_, _) => WithSelected(h => _service.Engine.ResumeAsync(h));
        var remove = new Button { Left = 466, Top = 8, Width = 80, Height = 28, Text = "Remove" };
        remove.Click += (_, _) => WithSelected(h => _service.Engine.RemoveAsync(h));
        topPanel.Controls.Add(addMagnet);
        topPanel.Controls.Add(addTorrent);
        topPanel.Controls.Add(pause);
        topPanel.Controls.Add(resume);
        topPanel.Controls.Add(remove);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _list.Columns.Add("Name", 300);
        _list.Columns.Add("Progress", 70);
        _list.Columns.Add("Status", 90);
        _list.Columns.Add("Down", 80);
        _list.Columns.Add("Up", 80);
        _list.Columns.Add("Peers", 50);
        _list.Columns.Add("Size", 90);
        _list.Columns.Add("Role", 60);

        Controls.Add(_list);
        Controls.Add(topPanel);

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();
    }

    private void AddMagnet()
    {
        using var dialog = new PromptDialog("Add Magnet Link", "Paste a magnet: link to start downloading:");
        var result = FindForm() is Form owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            try { _ = _service.Engine.StartDownloadAsync(dialog.Value.Trim()); }
            catch (Exception ex) { MessageBox.Show($"That didn't look like a valid magnet link:\n{ex.Message}", "Add Magnet"); }
        }
    }

    private void AddTorrentFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*" };
        var result = FindForm() is Form owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result == DialogResult.OK)
        {
            try { _ = _service.Engine.StartDownloadAsync(dialog.FileName); }
            catch (Exception ex) { MessageBox.Show($"Couldn't load that .torrent file:\n{ex.Message}", "Add Torrent"); }
        }
    }

    private void WithSelected(Func<string, Task> action)
    {
        if (_list.SelectedItem?.Tag is TransferSnapshot snapshot)
            _ = action(snapshot.InfoHashHex);
    }

    private void RefreshList()
    {
        var snapshots = _service.Engine.GetSnapshots();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in snapshots)
        {
            var item = new ListViewItem(s.Name) { Tag = s };
            item.SubItems.Add($"{s.ProgressPercent:0.0}%");
            item.SubItems.Add(DescribeState(s.State));
            item.SubItems.Add($"{P2PShareService.FormatSize(s.DownloadRateBps)}/s");
            item.SubItems.Add($"{P2PShareService.FormatSize(s.UploadRateBps)}/s");
            item.SubItems.Add(s.AvailablePeers.ToString());
            item.SubItems.Add(P2PShareService.FormatSize(s.TotalSize));
            item.SubItems.Add(s.IsSeedOnly ? "Seed" : "Leech");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private static string DescribeState(TorrentState state) => state switch
    {
        TorrentState.Downloading => "Downloading",
        TorrentState.Seeding => "Seeding",
        TorrentState.Hashing => "Checking",
        TorrentState.HashingPaused => "Checking (paused)",
        TorrentState.Metadata or TorrentState.FetchingHashes => "Fetching info",
        TorrentState.Paused => "Paused",
        TorrentState.Stopped => "Stopped",
        TorrentState.Stopping => "Stopping",
        TorrentState.Starting => "Starting",
        TorrentState.Error => "Error",
        _ => state.ToString(),
    };
}
