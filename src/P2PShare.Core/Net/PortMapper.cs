using System.Net;
using Mono.Nat;
using NatMapping = Mono.Nat.Mapping;
using NatProtocol = Mono.Nat.Protocol;

namespace P2PShare.Core.Net;

/// <summary>What kind of address the router reports on its internet-facing side.</summary>
public enum WanAddressKind
{
    Unknown,
    Public,

    /// <summary>RFC 1918 / link-local: the router itself sits behind another NAT (an ISP modem, say).</summary>
    Private,

    /// <summary>100.64.0.0/10: the ISP shares one public address between customers.</summary>
    CarrierGradeNat,
}

/// <summary>
/// Asks the home router (UPnP, and NAT-PMP where it speaks it) to forward one UDP port to this
/// machine. The DHT crawler learns about torrents from queries other nodes send it, and a router
/// that drops unsolicited inbound UDP hides most of them.
///
/// Everything here is best effort. Many networks have no such router, or have one with the feature
/// turned off, and that must never stop the app: problems are reported through the log callback.
/// </summary>
public sealed class PortMapper : IAsyncDisposable
{
    private static readonly TimeSpan DeviceCallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(5);

    private readonly int _udpPort;
    private readonly string _description;
    private readonly Action<string> _log;
    private readonly TimeSpan _renewInterval;
    private readonly TimeSpan _noDeviceNoticeAfter;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly HashSet<string> _seenDevices = new();
    private readonly HashSet<WanAddressKind> _warned = new();
    private readonly CancellationTokenSource _cts = new();
    private EventHandler<DeviceEventArgs>? _deviceFound;
    private Task? _backgroundTask;
    private bool _discoveryStarted;
    private bool _disposed;

    private sealed record Entry(INatDevice Device, NatMapping Mapping);

    public PortMapper(
        int udpPort,
        Action<string> log,
        TimeSpan? renewInterval = null,
        TimeSpan? noDeviceNoticeAfter = null,
        string description = "P2PShare DHT crawler")
    {
        if (udpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(udpPort));
        _udpPort = udpPort;
        _log = log;
        _description = description;
        // NAT-PMP leases last two hours by default and UPnP ones may be shortened by the router, so
        // asking again regularly keeps the forwarding alive for as long as the app runs.
        _renewInterval = renewInterval ?? TimeSpan.FromMinutes(30);
        _noDeviceNoticeAfter = noDeviceNoticeAfter ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Number of routers that currently forward the port.</summary>
    public int MappedDeviceCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Begins looking for routers; mappings are created as each one answers. Returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _discoveryStarted) return;
            _discoveryStarted = true;
        }

        _deviceFound = (_, e) => _ = AttachAsync(e.Device);
        NatUtility.DeviceFound += _deviceFound;
        try
        {
            NatUtility.StartDiscovery();
        }
        catch (Exception ex)
        {
            _log($"Port mapping: could not start router discovery ({ex.Message}).");
            return;
        }

        _backgroundTask = Task.Run(() => BackgroundAsync(_cts.Token));
    }

    private async Task BackgroundAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_noDeviceNoticeAfter, token).ConfigureAwait(false);
            if (MappedDeviceCount == 0 && !AnyDeviceSeen())
            {
                _log(
                    "Port mapping: no UPnP/NAT-PMP router answered. If this computer is behind a router, " +
                    $"forward UDP {_udpPort} to it by hand; otherwise the search index will grow slowly.");
            }

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_renewInterval, token).ConfigureAwait(false);
                await RenewAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool AnyDeviceSeen()
    {
        lock (_gate) return _seenDevices.Count > 0;
    }

    /// <summary>Maps the port on one router. Never throws.</summary>
    internal async Task AttachAsync(INatDevice device)
    {
        var key = $"{device.NatProtocol}@{device.DeviceEndpoint}";
        lock (_gate)
        {
            if (_disposed || !_seenDevices.Add(key)) return;
        }

        try
        {
            var wan = await GetWanAddressKindAsync(device).ConfigureAwait(false);

            var requested = new NatMapping(NatProtocol.Udp, _udpPort, _udpPort, 0, _description);
            var created = await device.CreatePortMapAsync(requested).WaitAsync(DeviceCallTimeout).ConfigureAwait(false);

            bool keep;
            lock (_gate)
            {
                keep = !_disposed;
                if (keep) _entries.Add(new Entry(device, created));
            }

            if (!keep)
            {
                // Disposed while the router was answering: take the mapping straight back out.
                await DeleteQuietlyAsync(device, created).ConfigureAwait(false);
                return;
            }

            _log($"Port mapping: UDP {created.PublicPort} -> this computer's {created.PrivatePort} created via {device.NatProtocol} on {device.DeviceEndpoint}.");

            if (created.PublicPort != _udpPort)
            {
                _log(
                    $"Port mapping: the router gave external port {created.PublicPort} instead of {_udpPort}; " +
                    "other nodes may not reach the crawler through it.");
            }

            WarnIfNotReachable(wan);
        }
        catch (Exception ex)
        {
            _log($"Port mapping via {device.NatProtocol} on {device.DeviceEndpoint} failed: {Describe(ex)}");
        }
    }

    private void WarnIfNotReachable(WanAddressKind wan)
    {
        // One router often answers over both UPnP and NAT-PMP; say each warning once.
        lock (_gate)
        {
            if (!_warned.Add(wan)) return;
        }

        switch (wan)
        {
            case WanAddressKind.Private:
                _log(
                    "Port mapping: the router reports a private address on its internet side, so it is itself behind " +
                    $"another router (an ISP modem, for example). The mapping works on the first router, but UDP {_udpPort} " +
                    "is only reachable from the internet if that other device forwards it too, or is put in bridge mode.");
                break;
            case WanAddressKind.CarrierGradeNat:
                _log(
                    "Port mapping: your ISP shares one public address between customers (carrier-grade NAT), so the " +
                    "mapping cannot make this port reachable from the internet. Ask the ISP for a public address, or accept a smaller index.");
                break;
        }
    }

    private static async Task<WanAddressKind> GetWanAddressKindAsync(INatDevice device)
    {
        try
        {
            var address = await device.GetExternalIPAsync().WaitAsync(DeviceCallTimeout).ConfigureAwait(false);
            return Classify(address);
        }
        catch (Exception)
        {
            return WanAddressKind.Unknown;
        }
    }

    public static WanAddressKind Classify(IPAddress? address)
    {
        if (address is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return WanAddressKind.Unknown;

        var b = address.GetAddressBytes();
        if (b.All(x => x == 0)) return WanAddressKind.Unknown; // 0.0.0.0: the router has no WAN link yet
        if (b[0] == 10) return WanAddressKind.Private;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return WanAddressKind.Private;
        if (b[0] == 192 && b[1] == 168) return WanAddressKind.Private;
        if (b[0] == 169 && b[1] == 254) return WanAddressKind.Private;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return WanAddressKind.CarrierGradeNat;
        if (b[0] == 127) return WanAddressKind.Unknown;
        return WanAddressKind.Public;
    }

    /// <summary>Asks every router again, so leases that would otherwise lapse stay alive.</summary>
    internal async Task RenewAsync()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();

        foreach (var entry in entries)
        {
            try
            {
                await entry.Device.CreatePortMapAsync(entry.Mapping).WaitAsync(DeviceCallTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"Port mapping: renewing UDP {_udpPort} on {entry.Device.DeviceEndpoint} failed: {Describe(ex)}");
            }
        }
    }

    private static string Describe(Exception ex) => ex is TimeoutException ? "the router did not answer in time" : ex.Message;

    private static async Task DeleteQuietlyAsync(INatDevice device, NatMapping mapping)
    {
        try
        {
            await device.DeletePortMapAsync(mapping).WaitAsync(DeleteTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Router gone, or it already forgot the mapping - nothing more to do on shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        bool unsubscribe;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.ToArray();
            _entries.Clear();
            unsubscribe = _discoveryStarted;
        }

        _cts.Cancel();
        if (_backgroundTask is not null)
        {
            try { await _backgroundTask.ConfigureAwait(false); }
            catch (Exception) { }
        }

        if (unsubscribe)
        {
            if (_deviceFound is not null) NatUtility.DeviceFound -= _deviceFound;
            try { NatUtility.StopDiscovery(); }
            catch (Exception) { }
        }

        // Leave the router as we found it, so a closed app doesn't leave a hole open.
        await Task.WhenAll(entries.Select(e => DeleteQuietlyAsync(e.Device, e.Mapping))).ConfigureAwait(false);
        _cts.Dispose();
    }
}
