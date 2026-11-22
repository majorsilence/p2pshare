using Majorsilence.Forms;
using Majorsilence.Forms.Drawing;
using P2PShare.Core;
using P2PShare.Desktop.Tabs;
using Timer = Majorsilence.Forms.Timer;

namespace P2PShare.Desktop;

public sealed class MainForm : Form
{
    private readonly P2PShareService _service;
    private readonly ToolStripStatusLabel _nodesLabel;
    private readonly ToolStripStatusLabel _speedLabel;
    private readonly ToolStripStatusLabel _indexLabel;
    private readonly Timer _statusTimer;

    public MainForm(P2PShareService service)
    {
        _service = service;

        Text = "P2PShare - DHT File Sharing";
        ClientSize = new System.Drawing.Size(1000, 680);
        MinimumSize = new System.Drawing.Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.WindowBackground;
        Font = new Font("Tahoma", 9f, bold: false, italic: false);

        // This backend docks children in the order they're added, so the Dock.Fill control must
        // be added first - otherwise it claims the whole client area before the top/bottom bars
        // reserve their space, and the tab strip ends up painted underneath the menu.
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTabPage("Search", new SearchTab(service)));
        tabs.TabPages.Add(MakeTabPage("Downloads", new DownloadsTab(service)));
        tabs.TabPages.Add(MakeTabPage("Library", new LibraryTab(service)));
        tabs.TabPages.Add(MakeTabPage("Network", new NetworkTab(service)));
        tabs.TabPages.Add(MakeTabPage("Settings", new SettingsTab(service)));
        Controls.Add(tabs);

        var statusStrip = new StatusStrip();
        _nodesLabel = new ToolStripStatusLabel("DHT nodes: 0");
        _speedLabel = new ToolStripStatusLabel("↓ 0 KB/s   ↑ 0 KB/s");
        _indexLabel = new ToolStripStatusLabel("Indexed: 0") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        statusStrip.Items.Add(_nodesLabel);
        statusStrip.Items.Add(new ToolStripStatusLabel("|"));
        statusStrip.Items.Add(_speedLabel);
        statusStrip.Items.Add(_indexLabel);
        Controls.Add(statusStrip);

        var menu = BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;

        _statusTimer = new Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatusBar();
        _statusTimer.Start();

        FormClosing += OnFormClosing;
    }

    private static TabPage MakeTabPage(string title, Control content)
    {
        var page = new TabPage(title);
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var transfers = new ToolStripMenuItem("&Transfers");
        transfers.DropDownItems.Add(new ToolStripMenuItem("Open &Downloads Folder", null, (_, _) => OpenDownloadsFolder()));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About P2PShare", null, (_, _) => ShowAbout()));

        menu.Items.Add(file);
        menu.Items.Add(transfers);
        menu.Items.Add(help);
        return menu;
    }

    private void OpenDownloadsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _service.Settings.DownloadsDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(_service.Settings.DownloadsDirectory, "Downloads Folder");
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "P2PShare\n\n" +
            "A file-sharing client in the spirit of the classic Napster / Kazaa / eMule era, " +
            "rebuilt on the BitTorrent mainline DHT. Search results come from a local index this " +
            "app builds itself by observing public DHT traffic - there is no central server.\n\n" +
            "Built with Majorsilence.Forms and MonoTorrent.",
            "About P2PShare",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void RefreshStatusBar()
    {
        _nodesLabel.Text = $"DHT nodes: {_service.Crawler.NodeCount}";
        _speedLabel.Text = $"↓ {P2PShareService.FormatSize(_service.Engine.TotalDownloadRateBps)}/s   ↑ {P2PShareService.FormatSize(_service.Engine.TotalUploadRateBps)}/s";
        _indexLabel.Text = $"Indexed: {_service.Index.TotalIndexed()}";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _statusTimer.Stop();
    }
}
