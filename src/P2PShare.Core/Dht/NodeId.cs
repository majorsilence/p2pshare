using System.Security.Cryptography;

namespace P2PShare.Core.Dht;

/// <summary>A 160-bit Mainline DHT node ID or info-hash, per BEP 5.</summary>
public readonly struct NodeId : IEquatable<NodeId>
{
    public const int Length = 20;

    private readonly byte[] _bytes;

    public NodeId(byte[] bytes)
    {
        if (bytes.Length != Length)
            throw new ArgumentException($"Node id must be {Length} bytes, got {bytes.Length}");
        _bytes = bytes;
    }

    public ReadOnlySpan<byte> Span => _bytes;

    public static NodeId Random()
    {
        var bytes = new byte[Length];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>A random id that shares no required relationship with any target (used for fan-out walks).</summary>
    public static NodeId RandomCloseTo(NodeId target)
    {
        // Flip a modest number of leading bits so the generated id sometimes routes
        // toward parts of the keyspace already represented in our table.
        var bytes = (byte[])target._bytes.Clone();
        var rnd = new Random();
        for (int i = 8; i < Length; i++)
            bytes[i] = (byte)rnd.Next(256);
        return new NodeId(bytes);
    }

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public string ToHex() => Convert.ToHexString(_bytes);

    public static NodeId FromHex(string hex) => new(Convert.FromHexString(hex));

    public bool Equals(NodeId other) => _bytes is not null && other._bytes is not null && Span.SequenceEqual(other.Span);

    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes is null) return 0;
        // First 4 bytes give ample distribution for a hash bucket.
        return BitConverter.ToInt32(_bytes, 0);
    }

    public override string ToString() => ToHex();
}
