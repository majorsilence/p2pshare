using System.Text.Json;
using P2PShare.Core.Net;
using P2PShare.Core.Settings;

namespace P2PShare.Core.Tests;

public class TrackerListTests
{
    [Fact]
    public void The_built_in_list_is_valid_unique_and_has_several_trackers()
    {
        var parsed = TrackerList.Parse(TrackerList.ToText(TrackerList.Defaults));

        Assert.Empty(parsed.Rejected);
        Assert.Equal(TrackerList.Defaults.Count, parsed.Valid.Count);
        Assert.True(TrackerList.Defaults.Count >= 5);
        Assert.Equal(TrackerList.Defaults.Count, TrackerList.Defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("udp://tracker.example.org:1337/announce")]
    [InlineData("udp://tracker.example.org:80")]
    [InlineData("http://tracker.example.org/announce")]
    [InlineData("http://tracker.example.org:8080/announce")]
    [InlineData("https://tracker.example.org/announce?key=abc")]
    [InlineData("  UDP://Tracker.Example.org:6969/announce  ")]
    public void Usable_urls_are_accepted(string line)
    {
        Assert.True(TrackerList.TryNormalize(line, out var url, out _));
        Assert.Equal(line.Trim(), url);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("tracker.example.org:80", "udp://")]
    [InlineData("ftp://tracker.example.org/announce", "udp://")]
    [InlineData("wss://tracker.example.org", "udp://")]
    [InlineData("udp://tracker.example.org/announce", "need a port")]
    [InlineData("udp://tracker.example.org:0/announce", "port")]
    [InlineData("udp://tracker.example.org:70000/announce", "valid URL")]
    [InlineData("http://user:secret@tracker.example.org/announce", "password")]
    [InlineData("udp://:1337/announce", "valid URL")]
    [InlineData("http://tracker.example.org/an\u0007nounce", "control")]
    public void Unusable_urls_are_rejected_with_a_reason(string line, string reasonFragment)
    {
        Assert.False(TrackerList.TryNormalize(line, out var url, out var reason));
        Assert.Equal("", url);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_skips_blank_and_comment_lines_and_handles_both_line_endings()
    {
        var parsed = TrackerList.Parse("# my trackers\r\n\r\nudp://a.example.org:1337/announce\r\n   # off for now\n  http://b.example.org/announce  \n");

        Assert.Empty(parsed.Rejected);
        Assert.Equal(new[] { "udp://a.example.org:1337/announce", "http://b.example.org/announce" }, parsed.Valid);
    }

    [Fact]
    public void Parse_drops_repeats_ignoring_case_but_keeps_first_seen_order()
    {
        var parsed = TrackerList.Parse(
            "udp://b.example.org:1/announce\nudp://a.example.org:1/announce\nUDP://B.EXAMPLE.ORG:1/ANNOUNCE");

        Assert.Equal(new[] { "udp://b.example.org:1/announce", "udp://a.example.org:1/announce" }, parsed.Valid);
        Assert.Empty(parsed.Rejected);
    }

    [Fact]
    public void Parse_reports_every_bad_line_instead_of_dropping_it_silently()
    {
        var parsed = TrackerList.Parse("udp://ok.example.org:1/announce\nnot a url\nftp://x.example.org/a\nudp://nopeport.example.org/a");

        Assert.Single(parsed.Valid);
        Assert.Equal(3, parsed.Rejected.Count);
        Assert.Equal("not a url", parsed.Rejected[0].Line);
        Assert.All(parsed.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    [Fact]
    public void Parse_caps_the_list_and_says_so()
    {
        var text = string.Join("\n", Enumerable.Range(0, TrackerList.MaxTrackers + 3).Select(i => $"udp://t{i}.example.org:1337/announce"));

        var parsed = TrackerList.Parse(text);

        Assert.Equal(TrackerList.MaxTrackers, parsed.Valid.Count);
        Assert.Equal(3, parsed.Rejected.Count);
        Assert.All(parsed.Rejected, r => Assert.Contains("more than", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_text_is_a_valid_empty_list()
    {
        var parsed = TrackerList.Parse("");

        Assert.Empty(parsed.Valid);
        Assert.Empty(parsed.Rejected);
        Assert.Empty(TrackerList.Parse(null).Valid);
    }

    [Fact]
    public void Merge_keeps_the_first_lists_order_and_adds_only_new_entries()
    {
        var merged = TrackerList.Merge(
            new[] { "udp://own.example.org:1/a", "udp://shared.example.org:1/a" },
            new[] { "UDP://SHARED.example.org:1/a", "udp://extra.example.org:1/a" });

        Assert.Equal(new[] { "udp://own.example.org:1/a", "udp://shared.example.org:1/a", "udp://extra.example.org:1/a" }, merged);
        Assert.Empty(TrackerList.Merge(null, null));
    }

    [Fact]
    public void A_lookup_uses_every_tracker_when_there_are_few()
    {
        var few = Enumerable.Range(0, TrackerList.MaxPerLookup).Select(i => $"udp://t{i}.example.org:1/a").ToList();

        Assert.Equal(few, TrackerList.PickForLookup(few));
    }

    [Fact]
    public void A_lookup_samples_a_few_distinct_trackers_when_there_are_many()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"udp://t{i}.example.org:1/a").ToList();

        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            var picked = TrackerList.PickForLookup(many);
            Assert.Equal(TrackerList.MaxPerLookup, picked.Count);
            Assert.Equal(TrackerList.MaxPerLookup, picked.Distinct().Count());
            Assert.All(picked, p => Assert.Contains(p, many));
            foreach (var p in picked) seen.Add(p);
        }

        // Load is spread: over many lookups far more than four distinct trackers get used.
        Assert.True(seen.Count > TrackerList.MaxPerLookup * 2, $"only {seen.Count} distinct trackers were ever picked");
    }
}

public class TrackerSettingsTests
{
    [Fact]
    public void New_settings_start_with_the_built_in_trackers_and_lookups_enabled()
    {
        using var dir = new TempDir();

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(TrackerList.Defaults, settings.Trackers);
        Assert.True(settings.UseTrackersForLookups);
    }

    [Fact]
    public void An_edited_list_survives_save_and_load()
    {
        using var dir = new TempDir();
        var settings = AppSettings.Load(dir.Path);
        settings.Trackers = new List<string> { "udp://mine.example.org:6969/announce" };
        settings.UseTrackersForLookups = false;
        settings.Save();

        var reloaded = AppSettings.Load(dir.Path);

        Assert.Equal(new[] { "udp://mine.example.org:6969/announce" }, reloaded.Trackers);
        Assert.False(reloaded.UseTrackersForLookups);
    }

    [Fact]
    public void A_settings_file_from_before_trackers_existed_gets_the_built_in_list()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ \"ListenPort\": 51999, \"SharedPaths\": [] }");

        var settings = AppSettings.Load(dir.Path);

        Assert.Equal(51999, settings.ListenPort);
        Assert.Equal(TrackerList.Defaults, settings.Trackers);
    }

    [Fact]
    public void A_deliberately_empty_list_stays_empty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ \"Trackers\": [] }");

        Assert.Empty(AppSettings.Load(dir.Path).Trackers);
    }

    [Fact]
    public void A_null_list_means_the_built_in_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ \"Trackers\": null }");

        Assert.Equal(TrackerList.Defaults, AppSettings.Load(dir.Path).Trackers);
    }

    [Fact]
    public void Junk_in_a_hand_edited_list_is_dropped_on_load()
    {
        using var dir = new TempDir();
        var json = JsonSerializer.Serialize(new
        {
            Trackers = new[] { "udp://good.example.org:1337/announce", "garbage", "ftp://nope.example.org/a", "UDP://GOOD.example.org:1337/announce" },
        });
        File.WriteAllText(dir.File("settings.json"), json);

        Assert.Equal(new[] { "udp://good.example.org:1337/announce" }, AppSettings.Load(dir.Path).Trackers);
    }
}
