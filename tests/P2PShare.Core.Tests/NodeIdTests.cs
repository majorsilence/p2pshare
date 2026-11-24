using P2PShare.Core.Dht;

namespace P2PShare.Core.Tests;

public class NodeIdTests
{
    [Fact]
    public void Hex_round_trips()
    {
        var id = NodeId.Random();
        Assert.Equal(id, NodeId.FromHex(id.ToHex()));
        Assert.Equal(40, id.ToHex().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    public void Wrong_length_is_rejected(int length)
    {
        Assert.Throws<ArgumentException>(() => new NodeId(new byte[length]));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = new NodeId(Net.Bytes(20, 7));
        var b = new NodeId(Net.Bytes(20, 7));
        var c = new NodeId(Net.Bytes(20, 8));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Default_value_equals_nothing_and_does_not_throw()
    {
        NodeId empty = default;
        Assert.False(empty.Equals(NodeId.Random()));
        Assert.Equal(0, empty.GetHashCode());
    }

    [Fact]
    public void ToBytes_returns_a_copy()
    {
        var id = new NodeId(Net.Bytes(20, 1));
        var copy = id.ToBytes();
        copy[0] = 99;
        Assert.Equal(1, id.Span[0]);
    }

    [Fact]
    public void RandomCloseTo_keeps_the_leading_bytes_of_the_target()
    {
        var target = NodeId.Random();
        var near = NodeId.RandomCloseTo(target);
        Assert.True(target.Span[..8].SequenceEqual(near.Span[..8]));
    }
}
