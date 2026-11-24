using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using P2PShare.Core;
using P2PShare.Core.Search;
using P2PShare.Core.Settings;
using P2PShare.Desktop;
using P2PShare.Desktop.Tabs;

namespace P2PShare.Desktop.Tests;

/// <summary>
/// Builds the real UI on Majorsilence.Forms' offscreen backend against an offline service. This
/// can't prove the Avalonia window looks right, but it does exercise every tab's construction,
/// layout and rendering code, which is where a typo or a bad API call would show up.
/// </summary>
public class UiSmokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "p2pshare-ui-tests", Guid.NewGuid().ToString("N"));

    static UiSmokeTests() => HeadlessRenderer.Use();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private P2PShareService NewService()
    {
        var settings = AppSettings.Load(_dir);
        settings.DownloadsDirectory = Path.Combine(_dir, "downloads");
        settings.CrawlerPort = 0;
        settings.ListenPort = FreeTcpPort();
        return new P2PShareService(settings, enableNetwork: false);
    }

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Every_tab_builds_lays_out_and_renders()
    {
        await using var service = NewService();
        await service.StartAsync();
        service.Index.UpsertResolved(new P2PShare.Core.Engine.FetchedMetadata(
            new string('a', 40), "Example Torrent", 123_456_789, new[] { ("a.bin", 1L) }));
        await service.AddDownloadAsync("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567");

        var form = new MainForm(service);
        var tabs = Assert.Single(form.Controls.OfType<TabControl>());
        Assert.Equal(5, tabs.TabPages.Count);

        var snapshotDir = Environment.GetEnvironmentVariable("P2PSHARE_UI_SNAPSHOTS");
        for (int i = 0; i < tabs.TabPages.Count; i++)
        {
            tabs.SelectedIndex = i;
            HeadlessRenderer.CapturePng(form, 1000, 680);            // first call lays out
            var png = HeadlessRenderer.CapturePng(form, 1000, 680);  // second is the settled frame

            Assert.True(png.Length > 2_000, $"tab {i} rendered an implausibly small image");
            if (!string.IsNullOrEmpty(snapshotDir))
            {
                Directory.CreateDirectory(snapshotDir);
                File.WriteAllBytes(Path.Combine(snapshotDir, $"tab{i}-{tabs.TabPages[i].Text}.png"), png);
            }
        }
    }

    [Fact]
    public async Task The_network_tab_shows_startup_messages_logged_before_it_existed()
    {
        await using var service = NewService();
        await service.StartAsync(); // logs "DHT crawler listening..." before any UI is built

        var tab = new NetworkTab(service);
        var form = new Form();
        form.Controls.Add(tab);
        HeadlessRenderer.CapturePng(form, 900, 600);

        tab.FlushLog(); // what the tab's 1-second UI timer does

        var log = FindLogBox(tab);
        Assert.Contains("DHT crawler listening", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_network_tab_log_is_capped()
    {
        await using var service = NewService();
        var tab = new NetworkTab(service);
        var form = new Form();
        form.Controls.Add(tab);
        HeadlessRenderer.CapturePng(form, 900, 600);

        // Reach the service's logger the way the real pipeline does: a failed share logs one line.
        for (int i = 0; i < 1200; i++)
        {
            await service.AddSharedPathAsync(Path.Combine(_dir, $"missing-{i}"));
            if (i % 100 == 0) tab.FlushLog();
        }
        tab.FlushLog();

        var lines = FindLogBox(tab).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(lines.Length, 1, 500);
        Assert.Contains("missing-1199", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Repopulating_a_list_keeps_the_same_row_selected()
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false };
        list.Columns.Add("Name", 200);
        var form = new Form();
        form.Controls.Add(list);

        var items = new[] { "aaa", "bbb", "ccc" };
        ListViewItem Row(string s) => new(s);
        list.Repopulate(items, s => s, Row);
        HeadlessRenderer.CapturePng(form, 400, 300);

        list.Items.Cast<ListViewItem>().Single(r => (string)r.Tag! == "bbb").Selected = true;
        Assert.Equal("bbb", list.SelectedItem?.Tag);

        list.Repopulate(items, s => s, Row);   // what the 1-second refresh timer does
        list.Repopulate(items, s => s, Row);

        Assert.Equal("bbb", list.SelectedItem?.Tag);
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void Repopulating_when_the_selected_row_has_gone_selects_nothing()
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false };
        list.Columns.Add("Name", 200);
        var form = new Form();
        form.Controls.Add(list);

        list.Repopulate(new[] { "aaa", "bbb" }, s => s, s => new ListViewItem(s));
        list.Items.Cast<ListViewItem>().Single(r => (string)r.Tag! == "bbb").Selected = true;

        list.Repopulate(new[] { "aaa" }, s => s, s => new ListViewItem(s));

        Assert.Null(list.SelectedItem);
    }

    private static TextBox FindLogBox(Control root)
    {
        TextBox? found = null;
        void Walk(Control c)
        {
            if (c is TextBox { Multiline: true } tb) found ??= tb;
            foreach (Control child in c.Controls) Walk(child);
        }
        Walk(root);
        return found ?? throw new InvalidOperationException("log box not found");
    }

    private static async Task<bool> Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
