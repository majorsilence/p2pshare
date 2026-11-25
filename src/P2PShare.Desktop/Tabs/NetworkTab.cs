using Majorsilence.Forms;
using Majorsilence.Forms.Drawing;
using P2PShare.Core;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop.Tabs;

public sealed class NetworkTab : UserControl
{
    // The log is a plain text box; without a cap a long-running crawler would grow it forever.
    private const int MaxLogLines = 500;
    private const int MaxPendingLogLines = 2000;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private readonly P2PShareService _service;
    private readonly Label _crawlerNodesLabel;
    private readonly Label _queriesLabel;
    private readonly Label _uniqueHashesLabel;
    private readonly Label _lookupsLabel;
    private readonly Label _samplingLabel;
    private readonly Label _engineDhtLabel;
    private readonly TextBox _log;
    private readonly Queue<string> _logLines = new();
    private readonly Timer _refreshTimer;

    public NetworkTab(P2PShareService service)
    {
        _service = service;
        BackColor = Theme.PanelBackground;

        var statsPanel = new Panel { Dock = DockStyle.Top, Height = 200 };

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
        _lookupsLabel = MakeStatLabel("Metadata lookups: 0 tried, 0 resolved, 0 waiting", 10, 120);
        _samplingLabel = MakeStatLabel("Sampling nodes for info-hashes: 0 asked, 0 answered, 0 received", 10, 144);
        _engineDhtLabel = MakeStatLabel("Download engine DHT: starting...", 10, 168);

        statsPanel.Controls.Add(explanation);
        statsPanel.Controls.Add(_crawlerNodesLabel);
        statsPanel.Controls.Add(_queriesLabel);
        statsPanel.Controls.Add(_uniqueHashesLabel);
        statsPanel.Controls.Add(_lookupsLabel);
        statsPanel.Controls.Add(_samplingLabel);
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

        // Replays what was logged before this tab existed (e.g. startup problems), then follows live.
        _service.SubscribeToActivity(OnActivity);

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();
    }

    private static Label MakeStatLabel(string text, int left, int top) =>
        new() { Left = left, Top = top, Width = 500, Text = text };

    // Entries arrive on background threads, and some (the replayed history) before this control's
    // window exists, so they are only queued here. The refresh timer, which runs on the UI thread,
    // moves them into the text box. That keeps control access single-threaded without depending on
    // being able to post to a window that may not be created yet.
    private void OnActivity(ActivityEntry entry)
    {
        _pendingLog.Enqueue($"[{entry.TimeLocal:HH:mm:ss}] {entry.Message}");
        while (_pendingLog.Count > MaxPendingLogLines && _pendingLog.TryDequeue(out _)) { }
    }

    /// <summary>Moves queued activity into the log box. Called by the refresh timer on the UI thread.</summary>
    internal void FlushLog()
    {
        while (_pendingLog.TryDequeue(out var line)) AppendLogLine(line);
    }

    private void AppendLogLine(string line)
    {
        _logLines.Enqueue(line);
        if (_logLines.Count > MaxLogLines)
        {
            while (_logLines.Count > MaxLogLines * 3 / 4) _logLines.Dequeue();
            _log.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
        }
        else
        {
            _log.AppendText(line + Environment.NewLine);
        }
    }

    private void RefreshStats()
    {
        FlushLog();
        _crawlerNodesLabel.Text = _service.Crawler.IsRunning
            ? $"Crawler DHT nodes known: {_service.Crawler.NodeCount}"
            : "Crawler: not running (see the activity log below)";
        _queriesLabel.Text = $"DHT queries observed: {_service.Crawler.QueriesObserved}";
        _uniqueHashesLabel.Text = $"Unique info-hashes observed: {_service.Crawler.UniqueInfoHashesObserved}";
        _lookupsLabel.Text = $"Metadata lookups: {_service.LookupsAttempted} tried, {_service.LookupsResolved} resolved, {_service.LookupsQueued} waiting";
        _samplingLabel.Text =
            $"Sampling nodes for info-hashes: {_service.Crawler.SampleQueriesSent} asked, " +
            $"{_service.Crawler.SampleReplies} answered, {_service.Crawler.SampledInfoHashes} received";
        _engineDhtLabel.Text = $"Download engine DHT: {_service.Engine.Dht.State}, {_service.Engine.Dht.NodeCount} node(s)";
    }
}
