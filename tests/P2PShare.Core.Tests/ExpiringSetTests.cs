using P2PShare.Core.Util;

namespace P2PShare.Core.Tests;

public class ExpiringSetTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
    }

    [Fact]
    public void Second_add_of_a_fresh_key_is_rejected()
    {
        var set = new ExpiringSet(10, TimeSpan.FromHours(1));
        Assert.True(set.TryAdd("abc"));
        Assert.False(set.TryAdd("abc"));
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var set = new ExpiringSet(10, TimeSpan.FromHours(1));
        Assert.True(set.TryAdd("ABCDEF"));
        Assert.False(set.TryAdd("abcdef"));
    }

    [Fact]
    public void A_key_can_be_added_again_once_its_ttl_has_passed()
    {
        var clock = new FakeClock();
        var set = new ExpiringSet(10, TimeSpan.FromHours(6), clock.Read);

        Assert.True(set.TryAdd("a"));
        clock.Now += TimeSpan.FromHours(5);
        Assert.False(set.TryAdd("a"));
        clock.Now += TimeSpan.FromHours(2);
        Assert.True(set.TryAdd("a"));
    }

    [Fact]
    public void Size_never_exceeds_capacity_and_the_oldest_keys_go_first()
    {
        var clock = new FakeClock();
        var set = new ExpiringSet(10, TimeSpan.FromDays(365), clock.Read);

        for (int i = 0; i < 25; i++)
        {
            Assert.True(set.TryAdd($"key{i}"));
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(set.Count <= 10);
        }

        // The newest key is still remembered; the very first one has been shed.
        Assert.False(set.TryAdd("key24"));
        Assert.True(set.TryAdd("key0"));
    }

    [Fact]
    public void Expired_entries_are_dropped_before_live_ones_when_full()
    {
        var clock = new FakeClock();
        var set = new ExpiringSet(4, TimeSpan.FromMinutes(10), clock.Read);

        set.TryAdd("old1");
        set.TryAdd("old2");
        clock.Now += TimeSpan.FromMinutes(11);
        set.TryAdd("new1");
        set.TryAdd("new2");
        set.TryAdd("new3"); // pushes past capacity -> the two expired entries are evicted

        Assert.Equal(3, set.Count);
        Assert.False(set.TryAdd("new1"));
    }

    [Fact]
    public void Invalid_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpiringSet(0, TimeSpan.FromHours(1)));
    }
}
