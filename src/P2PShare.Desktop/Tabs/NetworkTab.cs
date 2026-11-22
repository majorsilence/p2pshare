using Majorsilence.Forms;
using Majorsilence.Forms.Drawing;
using P2PShare.Core;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop.Tabs;

public sealed class NetworkTab : UserControl
{
    private readonly P2PShareService _service;
    private readonly Label _crawlerNodesLabel;
    private readonly Label _queriesLabel;
    private readonly Label _uniqueHashesLabel;
    private readonly Label _engineDhtLabel;
    private readonly TextBox _log;
    private readonly Timer _refreshTimer;

    public NetworkTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;

        var statsPanel = new Panel { Dock = DockStyle.Top, Height = 150 };

        var explanation = new Label
        {
            Left = 10,
            Top = 8,
            Width = 960,
            Height = 36,
            Text = "This app has no central server. The crawler below is its own small DHT node, watching the " +
                   "public mainline BitTorrent network and learning what torrents are circulating right now.",
        };

        _crawlerNodesLabel = MakeStatLabel("Crawler DHT nodes known: 0", 10, 48);
        _queriesLabel = MakeStatLabel("DHT queries observed: 0", 10, 72);
        _uniqueHashesLabel = MakeStatLabel("Unique info-hashes observed: 0", 10, 96);
        _engineDhtLabel = MakeStatLabel("Download engine DHT: starting...", 10, 120);

        statsPanel.Controls.Add(explanation);
        statsPanel.Controls.Add(_crawlerNodesLabel);
        statsPanel.Controls.Add(_queriesLabel);
        statsPanel.Controls.Add(_uniqueHashesLabel);
        statsPanel.Controls.Add(_engineDhtLabel);

        var logLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Text = "Activity log:" };

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = Theme.ConsoleBackground,
            ForeColor = Theme.ConsoleText,
            Font = new Font("Consolas", 9f, bold: false, italic: false),
        };

        Controls.Add(_log);
        Controls.Add(logLabel);
        Controls.Add(statsPanel);

        _service.ActivityLog += OnActivityLog;

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private static Label MakeStatLabel(string text, int left, int top) =>
        new() { Left = left, Top = top, Width = 500, Text = text };

    private void OnActivityLog(string message)
    {
        BeginInvoke(() =>
        {
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        });
    }

    private void RefreshStats()
    {
        _crawlerNodesLabel.Text = $"Crawler DHT nodes known: {_service.Crawler.NodeCount}";
        _queriesLabel.Text = $"DHT queries observed: {_service.Crawler.QueriesObserved}";
        _uniqueHashesLabel.Text = $"Unique info-hashes observed: {_service.Crawler.UniqueInfoHashesObserved}";
        _engineDhtLabel.Text = $"Download engine DHT: {_service.Engine.Dht.State}, {_service.Engine.Dht.NodeCount} node(s)";
    }
}
