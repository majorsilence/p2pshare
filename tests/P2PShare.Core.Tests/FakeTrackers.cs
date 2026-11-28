using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MonoTorrent.BEncoding;
using P2PShare.Core.Net;

namespace P2PShare.Core.Tests;

/// <summary>A BEP 15 tracker on loopback that answers connect and scrape requests.</summary>
internal sealed class FakeUdpTracker : IAsyncDisposable
{
    private const long ConnectionId = 0x1122334455667788;

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _received;

    public ConcurrentDictionary<string, ScrapeCounts> Known { get; } = new();
    public ConcurrentQueue<int> ScrapeBatchSizes { get; } = new();

    public bool Silent { get; set; }
    public bool ErrorOnScrape { get; set; }
    public bool SendStrayReplyFirst { get; set; }
    public int IgnoreFirstDatagrams { get; set; }
    public int Received => Volatile.Read(ref _received);

    public string Url => $"udp://127.0.0.1:{((IPEndPoint)_udp.Client.LocalEndPoint!).Port}/announce";

    public FakeUdpTracker() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var request = await _udp.ReceiveAsync(_cts.Token);
                int number = Interlocked.Increment(ref _received);
                if (Silent || number <= IgnoreFirstDatagrams) continue;

                var data = request.Buffer;
                var action = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8));
                var transaction = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12));

                if (SendStrayReplyFirst)
                    await _udp.SendAsync(Header(action, transaction + 1, 8), request.RemoteEndPoint);

                if (action == 0 && BinaryPrimitives.ReadInt64BigEndian(data) == 0x41727101980)
                {
                    var reply = Header(0, transaction, 8);
                    BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(8), ConnectionId);
                    await _udp.SendAsync(reply, request.RemoteEndPoint);
                }
                else if (action == 2 && BinaryPrimitives.ReadInt64BigEndian(data) == ConnectionId)
                {
                    if (ErrorOnScrape)
                    {
                        var error = Header(3, transaction, 4);
                        Encoding.ASCII.GetBytes("nope").CopyTo(error, 8);
                        await _udp.SendAsync(error, request.RemoteEndPoint);
                        continue;
                    }

                    int count = (data.Length - 16) / 20;
                    ScrapeBatchSizes.Enqueue(count);
                    var reply = Header(2, transaction, 12 * count);
                    for (int i = 0; i < count; i++)
                    {
                        var hash = Convert.ToHexString(data, 16 + 20 * i, 20).ToLowerInvariant();
                        Known.TryGetValue(hash, out var c);
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8 + 12 * i), c.Seeders);
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(12 + 12 * i), c.Completed);
                        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(16 + 12 * i), c.Leechers);
                    }
                    await _udp.SendAsync(reply, request.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static byte[] Header(int action, int transaction, int payload)
    {
        var buffer = new byte[8 + payload];
        BinaryPrimitives.WriteInt32BigEndian(buffer, action);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), transaction);
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop;
        _udp.Dispose();
    }
}

/// <summary>A BEP 48 scrape endpoint on loopback, just enough HTTP for HttpClient.</summary>
internal sealed class FakeHttpTracker : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public Dictionary<string, ScrapeCounts> Known { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public bool Fail { get; set; }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/announce";

    public FakeHttpTracker()
    {
        _listener.Start();
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                int read = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read), _cts.Token);
                    if (n == 0) break;
                    read += n;
                }

                var target = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                Requests.Enqueue(target);

                var body = Respond(target).Encode();
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _cts.Token);
                await stream.WriteAsync(body, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private BEncodedDictionary Respond(string target)
    {
        if (Fail) return new BEncodedDictionary { { "failure reason", new BEncodedString("no scraping here") } };

        var files = new BEncodedDictionary();
        foreach (var pair in target[(target.IndexOf('?') + 1)..].Split('&'))
        {
            if (!pair.StartsWith("info_hash=", StringComparison.Ordinal)) continue;
            var hash = DecodePercent(pair["info_hash=".Length..]);
            if (Known.TryGetValue(Convert.ToHexString(hash).ToLowerInvariant(), out var c))
            {
                files.Add(new BEncodedString(hash), new BEncodedDictionary
                {
                    { "complete", new BEncodedNumber(c.Seeders) },
                    { "downloaded", new BEncodedNumber(c.Completed) },
                    { "incomplete", new BEncodedNumber(c.Leechers) },
                });
            }
        }
        return new BEncodedDictionary { { "files", files } };
    }

    private static byte[] DecodePercent(string encoded)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%') { bytes.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.Add((byte)encoded[i]);
        }
        return bytes.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _loop;
    }
}
