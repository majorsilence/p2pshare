using System.Collections.Concurrent;
using System.Net;
using Mono.Nat;
using P2PShare.Core.Net;
using NatMapping = Mono.Nat.Mapping;
using NatProtocol = Mono.Nat.Protocol;

namespace P2PShare.Core.Tests;

/// <summary>
/// The router side is a fake <see cref="INatDevice"/>, so none of this touches a real network. The
/// real UPnP/NAT-PMP exchange can only be checked on a machine that has such a router.
/// </summary>
public class PortMapperTests
{
    private sealed class FakeRouter : INatDevice
    {
        public IPEndPoint DeviceEndpoint { get; init; } = new(IPAddress.Parse("192.168.0.1"), 1900);
        public DateTime LastSeen => DateTime.UtcNow;
        public Mono.Nat.NatProtocol NatProtocol { get; init; } = Mono.Nat.NatProtocol.Upnp;

        public IPAddress ExternalIp { get; set; } = IPAddress.Parse("8.8.4.4");
        public bool FailExternalIp { get; set; }
        public bool FailCreate { get; set; }
        public bool FailDelete { get; set; }
        public int? AssignedPublicPort { get; set; }
        public TaskCompletionSource? CreateGate { get; set; }

        public ConcurrentQueue<NatMapping> Created { get; } = new();
        public ConcurrentQueue<NatMapping> Deleted { get; } = new();

        public async Task<NatMapping> CreatePortMapAsync(NatMapping mapping)
        {
            if (CreateGate is not null) await CreateGate.Task;
            if (FailCreate) throw new MappingException(ErrorCode.NotAuthorizedOrRefused, "UPnP is switched off on this router");
            Created.Enqueue(mapping);
            return new NatMapping(mapping.Protocol, mapping.PrivatePort, AssignedPublicPort ?? mapping.PublicPort, mapping.Lifetime, mapping.Description);
        }

        public Task<NatMapping> DeletePortMapAsync(NatMapping mapping)
        {
            if (FailDelete) throw new InvalidOperationException("router went away");
            Deleted.Enqueue(mapping);
            return Task.FromResult(mapping);
        }

        public Task<IPAddress> GetExternalIPAsync() =>
            FailExternalIp ? throw new InvalidOperationException("no WAN") : Task.FromResult(ExternalIp);

        public Task<NatMapping[]> GetAllMappingsAsync() => Task.FromResult(Array.Empty<NatMapping>());

        public Task<NatMapping> GetSpecificMappingAsync(NatProtocol protocol, int publicPort) =>
            throw new NotSupportedException();
    }

    private static (PortMapper Mapper, ConcurrentQueue<string> Log) NewMapper(int port = 6969)
    {
        var log = new ConcurrentQueue<string>();
        return (new PortMapper(port, log.Enqueue), log);
    }

    private static bool Said(ConcurrentQueue<string> log, string fragment) =>
        log.Any(l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("8.8.8.8", WanAddressKind.Public)]
    [InlineData("172.32.0.1", WanAddressKind.Public)]
    [InlineData("100.128.0.1", WanAddressKind.Public)]
    [InlineData("10.1.2.3", WanAddressKind.Private)]
    [InlineData("172.16.0.1", WanAddressKind.Private)]
    [InlineData("172.31.255.254", WanAddressKind.Private)]
    [InlineData("192.168.0.5", WanAddressKind.Private)]
    [InlineData("169.254.9.9", WanAddressKind.Private)]
    [InlineData("100.64.0.1", WanAddressKind.CarrierGradeNat)]
    [InlineData("100.127.255.255", WanAddressKind.CarrierGradeNat)]
    [InlineData("0.0.0.0", WanAddressKind.Unknown)]
    [InlineData("127.0.0.1", WanAddressKind.Unknown)]
    [InlineData("::1", WanAddressKind.Unknown)]
    public void Wan_addresses_are_classified(string address, WanAddressKind expected) =>
        Assert.Equal(expected, PortMapper.Classify(IPAddress.Parse(address)));

    [Fact]
    public void A_missing_address_is_unknown() => Assert.Equal(WanAddressKind.Unknown, PortMapper.Classify(null));

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void An_invalid_port_is_rejected(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortMapper(port, _ => { }));

    [Fact]
    public async Task A_router_that_answers_gets_a_udp_mapping_for_the_port()
    {
        var (mapper, log) = NewMapper(6969);
        await using var _ = mapper;
        var router = new FakeRouter();

        await mapper.AttachAsync(router);

        var asked = Assert.Single(router.Created);
        Assert.Equal(NatProtocol.Udp, asked.Protocol);
        Assert.Equal(6969, asked.PrivatePort);
        Assert.Equal(6969, asked.PublicPort);
        Assert.Equal(1, mapper.MappedDeviceCount);
        Assert.True(Said(log, "UDP 6969"), string.Join("\n", log));
        Assert.False(Said(log, "another router"));
        Assert.False(Said(log, "carrier-grade"));
    }

    [Fact]
    public async Task Both_a_upnp_and_a_nat_pmp_router_are_used()
    {
        var (mapper, _) = NewMapper();
        await using var _ = mapper;
        var upnp = new FakeRouter();
        var pmp = new FakeRouter { NatProtocol = Mono.Nat.NatProtocol.Pmp, DeviceEndpoint = new IPEndPoint(IPAddress.Parse("192.168.0.1"), 5351) };

        await Task.WhenAll(mapper.AttachAsync(upnp), mapper.AttachAsync(pmp));

        Assert.Equal(2, mapper.MappedDeviceCount);
        Assert.Single(upnp.Created);
        Assert.Single(pmp.Created);
    }

    [Fact]
    public async Task The_same_router_announcing_twice_is_mapped_once()
    {
        var (mapper, _) = NewMapper();
        await using var _ = mapper;
        var router = new FakeRouter();

        await mapper.AttachAsync(router);
        await mapper.AttachAsync(router);

        Assert.Single(router.Created);
        Assert.Equal(1, mapper.MappedDeviceCount);
    }

    [Fact]
    public async Task A_router_behind_another_router_gets_a_warning_but_is_still_mapped()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;
        var router = new FakeRouter { ExternalIp = IPAddress.Parse("192.168.1.2") };

        await mapper.AttachAsync(router);

        Assert.Equal(1, mapper.MappedDeviceCount);
        Assert.True(Said(log, "another router"), string.Join("\n", log));
        Assert.False(Said(log, "192.168.1.2"), "the router's address should not be written to the log");
    }

    [Fact]
    public async Task One_router_answering_over_two_protocols_is_warned_about_once()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;
        var upnp = new FakeRouter { ExternalIp = IPAddress.Parse("10.0.0.2") };
        var pmp = new FakeRouter { ExternalIp = IPAddress.Parse("10.0.0.2"), NatProtocol = Mono.Nat.NatProtocol.Pmp, DeviceEndpoint = new IPEndPoint(IPAddress.Parse("192.168.0.1"), 5351) };

        await mapper.AttachAsync(upnp);
        await mapper.AttachAsync(pmp);

        Assert.Equal(2, mapper.MappedDeviceCount);
        Assert.Equal(1, log.Count(l => l.Contains("another router", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Carrier_grade_nat_gets_its_own_warning()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;

        await mapper.AttachAsync(new FakeRouter { ExternalIp = IPAddress.Parse("100.70.1.1") });

        Assert.True(Said(log, "carrier-grade"), string.Join("\n", log));
    }

    [Fact]
    public async Task A_router_that_hands_out_another_external_port_is_flagged()
    {
        var (mapper, log) = NewMapper(6969);
        await using var _ = mapper;

        await mapper.AttachAsync(new FakeRouter { AssignedPublicPort = 40123 });

        Assert.True(Said(log, "40123"), string.Join("\n", log));
        Assert.True(Said(log, "instead of 6969"), string.Join("\n", log));
    }

    [Fact]
    public async Task A_router_that_refuses_is_logged_and_never_throws()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;

        await mapper.AttachAsync(new FakeRouter { FailCreate = true });

        Assert.Equal(0, mapper.MappedDeviceCount);
        Assert.True(Said(log, "failed"), string.Join("\n", log));
        Assert.True(Said(log, "switched off"), string.Join("\n", log));
    }

    [Fact]
    public async Task An_unreadable_wan_address_does_not_stop_the_mapping()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;
        var router = new FakeRouter { FailExternalIp = true };

        await mapper.AttachAsync(router);

        Assert.Equal(1, mapper.MappedDeviceCount);
        Assert.False(Said(log, "another router"));
    }

    [Fact]
    public async Task Renewing_asks_every_router_again_and_survives_a_failure()
    {
        var (mapper, log) = NewMapper();
        await using var _ = mapper;
        var good = new FakeRouter();
        var flaky = new FakeRouter { NatProtocol = Mono.Nat.NatProtocol.Pmp, DeviceEndpoint = new IPEndPoint(IPAddress.Parse("192.168.0.1"), 5351) };
        await mapper.AttachAsync(good);
        await mapper.AttachAsync(flaky);

        flaky.FailCreate = true;
        await mapper.RenewAsync();

        Assert.Equal(2, good.Created.Count);
        Assert.Single(flaky.Created);
        Assert.True(Said(log, "renewing"), string.Join("\n", log));
    }

    [Fact]
    public async Task Disposing_takes_the_mapping_back_out_of_the_router()
    {
        var (mapper, _) = NewMapper(6969);
        var router = new FakeRouter();
        await mapper.AttachAsync(router);

        await mapper.DisposeAsync();

        var removed = Assert.Single(router.Deleted);
        Assert.Equal(NatProtocol.Udp, removed.Protocol);
        Assert.Equal(6969, removed.PrivatePort);
        Assert.Equal(0, mapper.MappedDeviceCount);
    }

    [Fact]
    public async Task Disposing_survives_a_router_that_has_gone_away_and_is_idempotent()
    {
        var (mapper, _) = NewMapper();
        await mapper.AttachAsync(new FakeRouter { FailDelete = true });

        await mapper.DisposeAsync();
        await mapper.DisposeAsync();
    }

    [Fact]
    public async Task A_router_that_answers_after_shutdown_is_cleaned_up_again()
    {
        var (mapper, _) = NewMapper();
        var gate = new TaskCompletionSource();
        var router = new FakeRouter { CreateGate = gate };

        var attach = mapper.AttachAsync(router);   // router is slow to answer...
        await mapper.DisposeAsync();               // ...and the app closes meanwhile
        gate.SetResult();
        await attach;

        Assert.Single(router.Created);
        Assert.Single(router.Deleted);             // the late mapping is removed, not left open
        Assert.Equal(0, mapper.MappedDeviceCount);
    }

    [Fact]
    public async Task A_router_announcing_after_shutdown_is_ignored()
    {
        var (mapper, _) = NewMapper();
        await mapper.DisposeAsync();
        var router = new FakeRouter();

        await mapper.AttachAsync(router);

        Assert.Empty(router.Created);
    }
}
