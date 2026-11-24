using Majorsilence.Forms;
using P2PShare.Core;

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

        var hint = new Label { Left = 12, Top = 292, Width = 700, Height = 20, Text = "Port and bandwidth changes take effect the next time P2PShare starts." };

        var save = new Button { Left = 12, Top = 320, Width = 120, Height = 30, Text = "Save Settings" };
        save.Click += (_, _) => Save();

        Controls.Add(downloadsGroup);
        Controls.Add(bandwidthGroup);
        Controls.Add(portsGroup);
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
