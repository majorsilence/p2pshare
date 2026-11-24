using Majorsilence.Forms;
using Majorsilence.Forms.Drawing;
using P2PShare.Core;
using P2PShare.Core.Net;

namespace P2PShare.Desktop.Tabs;

public sealed class SettingsTab : UserControl
{
    private readonly P2PShareService _service;
    private readonly TextBox _downloadsPath;
    private readonly NumericUpDown _maxDownload;
    private readonly NumericUpDown _maxUpload;
    private readonly NumericUpDown _listenPort;
    private readonly NumericUpDown _dhtPort;
    private readonly NumericUpDown _crawlerPort;
    private readonly TextBox _trackers;
    private readonly CheckBox _trackersForLookups;

    public SettingsTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;
        AutoScroll = true;

        var downloadsGroup = new GroupBox { Left = 12, Top = 12, Width = 700, Height = 70, Text = "Downloads" };
        _downloadsPath = new TextBox { Left = 12, Top = 28, Width = 500, ReadOnly = true, Text = service.Settings.DownloadsDirectory };
        var browse = new Button { Left = 520, Top = 26, Width = 100, Height = 26, Text = "Change..." };
        browse.Click += (_, _) => ChangeDownloadsFolder();
        downloadsGroup.Controls.Add(_downloadsPath);
        downloadsGroup.Controls.Add(browse);

        var bandwidthGroup = new GroupBox { Left = 12, Top = 92, Width = 700, Height = 90, Text = "Bandwidth limits (0 = unlimited)" };
        var maxDownLabel = new Label { Left = 12, Top = 30, Width = 160, Text = "Max download (KB/s):" };
        _maxDownload = new NumericUpDown { Left = 180, Top = 28, Width = 100, Maximum = 1_000_000, Value = Clamp(service.Settings.MaxDownloadKBps, 0, 1_000_000) };
        var maxUpLabel = new Label { Left = 300, Top = 30, Width = 150, Text = "Max upload (KB/s):" };
        _maxUpload = new NumericUpDown { Left = 460, Top = 28, Width = 100, Maximum = 1_000_000, Value = Clamp(service.Settings.MaxUploadKBps, 0, 1_000_000) };
        bandwidthGroup.Controls.Add(maxDownLabel);
        bandwidthGroup.Controls.Add(_maxDownload);
        bandwidthGroup.Controls.Add(maxUpLabel);
        bandwidthGroup.Controls.Add(_maxUpload);

        var portsGroup = new GroupBox { Left = 12, Top = 192, Width = 700, Height = 90, Text = "Network ports" };
        var listenLabel = new Label { Left = 12, Top = 30, Width = 110, Text = "Listen port:" };
        _listenPort = new NumericUpDown { Left = 130, Top = 28, Width = 90, Minimum = 1024, Maximum = 65535, Value = ClampPort(service.Settings.ListenPort) };
        var dhtLabel = new Label { Left = 240, Top = 30, Width = 90, Text = "DHT port:" };
        _dhtPort = new NumericUpDown { Left = 335, Top = 28, Width = 90, Minimum = 1024, Maximum = 65535, Value = ClampPort(service.Settings.DhtPort) };
        var crawlerLabel = new Label { Left = 445, Top = 30, Width = 100, Text = "Crawler port:" };
        _crawlerPort = new NumericUpDown { Left = 550, Top = 28, Width = 90, Minimum = 1024, Maximum = 65535, Value = ClampPort(service.Settings.CrawlerPort) };
        portsGroup.Controls.Add(listenLabel);
        portsGroup.Controls.Add(_listenPort);
        portsGroup.Controls.Add(dhtLabel);
        portsGroup.Controls.Add(_dhtPort);
        portsGroup.Controls.Add(crawlerLabel);
        portsGroup.Controls.Add(_crawlerPort);

        var trackersGroup = new GroupBox { Left = 12, Top = 292, Width = 700, Height = 205, Text = "Trackers" };
        var trackersHelp = new Label
        {
            Left = 12, Top = 22, Width = 676, Height = 18,
            Text = "One announce URL per line (udp://, http:// or https://). Lines starting with # are ignored. Empty = DHT only.",
        };
        string trackerText;
        lock (service.Settings.SyncRoot) trackerText = TrackerList.ToText(service.Settings.Trackers);
        _trackers = new TextBox
        {
            Left = 12, Top = 44, Width = 676, Height = 108,
            Multiline = true,
            Font = new Font("Consolas", 9f, bold: false, italic: false),
            Text = trackerText,
        };
        _trackersForLookups = new CheckBox
        {
            Left = 12, Top = 164, Width = 520, Height = 24,
            Text = "Also ask a few trackers for peers when looking up search results",
            Checked = service.Settings.UseTrackersForLookups,
        };
        var resetTrackers = new Button { Left = 560, Top = 162, Width = 128, Height = 26, Text = "Reset to defaults" };
        resetTrackers.Click += (_, _) => _trackers.Text = TrackerList.ToText(TrackerList.Defaults);
        trackersGroup.Controls.Add(trackersHelp);
        trackersGroup.Controls.Add(_trackers);
        trackersGroup.Controls.Add(_trackersForLookups);
        trackersGroup.Controls.Add(resetTrackers);

        var hint = new Label
        {
            Left = 12, Top = 507, Width = 700, Height = 20,
            Text = "Ports and bandwidth apply on the next start. Tracker edits apply to anything added afterwards.",
        };

        var save = new Button { Left = 12, Top = 535, Width = 120, Height = 30, Text = "Save Settings" };
        save.Click += (_, _) => Save();

        Controls.Add(downloadsGroup);
        Controls.Add(bandwidthGroup);
        Controls.Add(portsGroup);
        Controls.Add(trackersGroup);
        Controls.Add(hint);
        Controls.Add(save);
    }

    // A hand-edited settings.json can hold values outside what the controls accept, and assigning
    // one to a NumericUpDown throws - which would stop the whole window from opening.
    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static int ClampPort(int port) => Math.Clamp(port, 1024, 65535);

    private void ChangeDownloadsFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose where downloaded files are saved", SelectedPath = _service.Settings.DownloadsDirectory };
        var result = FindForm() is Form owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            _downloadsPath.Text = dialog.SelectedPath;
    }

    private void Save()
    {
        // Check the tracker list first, so a typo is pointed out instead of silently dropped.
        var trackers = TrackerList.Parse(_trackers.Text);
        if (trackers.Rejected.Count > 0)
        {
            var shown = string.Join(Environment.NewLine, trackers.Rejected.Take(5).Select(r => $"  {r.Line}  ({r.Reason})"));
            var more = trackers.Rejected.Count > 5 ? $"{Environment.NewLine}  ...and {trackers.Rejected.Count - 5} more" : "";
            MessageBox.Show($"These tracker lines can't be used, so nothing was saved:{Environment.NewLine}{shown}{more}", "P2PShare");
            return;
        }

        lock (_service.Settings.SyncRoot)
        {
            _service.Settings.Trackers = trackers.Valid.ToList();
            _service.Settings.UseTrackersForLookups = _trackersForLookups.Checked;
        }

        _service.Settings.DownloadsDirectory = _downloadsPath.Text;
        _service.Settings.MaxDownloadKBps = (int)_maxDownload.Value;
        _service.Settings.MaxUploadKBps = (int)_maxUpload.Value;
        _service.Settings.ListenPort = (int)_listenPort.Value;
        _service.Settings.DhtPort = (int)_dhtPort.Value;
        _service.Settings.CrawlerPort = (int)_crawlerPort.Value;
        _service.Settings.Save();
        MessageBox.Show("Settings saved.", "P2PShare");
    }
}
