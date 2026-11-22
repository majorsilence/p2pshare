using Majorsilence.Forms;
using P2PShare.Core;
using P2PShare.Core.Engine;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop.Tabs;

public sealed class LibraryTab : UserControl
{
    private readonly P2PShareService _service;
    private readonly ListView _list;
    private readonly Timer _refreshTimer;

    public LibraryTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44 };
        var addFolder = new Button { Left = 10, Top = 8, Width = 140, Height = 28, Text = "Share Folder..." };
        addFolder.Click += (_, _) => ShareFolder();
        var addFile = new Button { Left = 158, Top = 8, Width = 120, Height = 28, Text = "Share File..." };
        addFile.Click += (_, _) => ShareFile();
        topPanel.Controls.Add(addFolder);
        topPanel.Controls.Add(addFile);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "Shared items are seeded over BitTorrent and announced on the DHT so other peers can find them.",
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
        };
        _list.Columns.Add("Name", 280);
        _list.Columns.Add("Status", 100);
        _list.Columns.Add("Up", 90);
        _list.Columns.Add("Peers", 60);
        _list.Columns.Add("Size", 100);
        _list.Columns.Add("Path", 260);

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(topPanel);

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();
    }

    private async void ShareFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder to share" };
        var result = FindForm() is Form owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            await ShareAndReport(dialog.SelectedPath);
    }

    private async void ShareFile()
    {
        using var dialog = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
        var result = FindForm() is Form owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
            await ShareAndReport(dialog.FileName);
    }

    private async Task ShareAndReport(string path)
    {
        var ok = await _service.AddSharedPathAsync(path);
        if (!ok) MessageBox.Show($"Couldn't start sharing:\n{path}", "Share");
        RefreshList();
    }

    private void RefreshList()
    {
        var shared = new HashSet<string>(_service.Settings.SharedPaths, StringComparer.OrdinalIgnoreCase);
        var snapshots = _service.Engine.GetSnapshots().Where(s => s.IsSeedOnly).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in snapshots)
        {
            var item = new ListViewItem(s.Name) { Tag = s };
            item.SubItems.Add(s.State.ToString());
            item.SubItems.Add($"{P2PShareService.FormatSize(s.UploadRateBps)}/s");
            item.SubItems.Add(s.AvailablePeers.ToString());
            item.SubItems.Add(P2PShareService.FormatSize(s.TotalSize));
            item.SubItems.Add(s.SavePath);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }
}
