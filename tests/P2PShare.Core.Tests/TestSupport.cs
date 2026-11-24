using System.Net;
using System.Net.Sockets;
using MonoTorrent.BEncoding;

namespace P2PShare.Core.Tests;

/// <summary>A scratch directory that is deleted when the test finishes.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "p2pshare-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* a straggling file handle must not fail the test run */ }
    }
}

internal static class Net
{
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>A KRPC query datagram as another DHT node would send it.</summary>
    public static BEncodedDictionary KrpcQuery(string method, BEncodedDictionary args, string transactionId = "aa") => new()
    {
        { "t", new BEncodedString(transactionId) },
        { "y", new BEncodedString("q") },
        { "q", new BEncodedString(method) },
        { "a", args },
    };

    /// <summary>Sends one datagram to the node and waits for its reply (or fails after a timeout).</summary>
    public static async Task<BEncodedDictionary> RoundTripAsync(UdpClient client, int port, BEncodedDictionary message)
    {
        await client.SendAsync(message.Encode(), new IPEndPoint(IPAddress.Loopback, port));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await client.ReceiveAsync(cts.Token);
        return BEncodedValue.Decode<BEncodedDictionary>(reply.Buffer);
    }

    public static UdpClient NewLoopbackClient() => new(new IPEndPoint(IPAddress.Loopback, 0));

    public static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
