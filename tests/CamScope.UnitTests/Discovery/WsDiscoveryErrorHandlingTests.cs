using System.Net;
using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Discovery.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Discovery;

public class WsDiscoveryErrorHandlingTests
{
    [Theory]
    [InlineData("192.168.1.100", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.20.30.40", "255.0.0.0", "10.255.255.255")]
    [InlineData("172.16.5.10", "255.255.0.0", "172.16.255.255")]
    [InlineData("192.168.1.5", "255.255.255.128", "192.168.1.127")]
    [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.255")]
    public void CalculateBroadcastAddress_ComputesAccurateSubnetBroadcast(string ip, string mask, string expectedBroadcast)
    {
        var broadcast = WsDiscoveryEngine.CalculateBroadcastAddress(IPAddress.Parse(ip), mask);
        Assert.Equal(IPAddress.Parse(expectedBroadcast), broadcast);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCancelled_ReportsCancelledStatus()
    {
        var engine = new WsDiscoveryEngine(NullLogger<WsDiscoveryEngine>.Instance);
        var iface = new NetworkInterfaceInfo
        {
            Id = "lo0",
            Name = "lo0",
            IPv4Address = "127.0.0.1",
            SubnetMask = "255.0.0.0",
            SupportsMulticast = true
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var devices = await engine.DiscoverAsync(iface, cts.Token);

        Assert.Equal(DiscoveryStatus.Cancelled, engine.Status);
        Assert.Empty(devices);
    }

    [Fact]
    public async Task DiscoverAsync_WithInvalidIp_ThrowsArgumentException()
    {
        var engine = new WsDiscoveryEngine(NullLogger<WsDiscoveryEngine>.Instance);
        var iface = new NetworkInterfaceInfo
        {
            Id = "bad0",
            Name = "bad0",
            IPv4Address = "not-an-ip",
            SubnetMask = "255.255.255.0"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => engine.DiscoverAsync(iface));
    }
}
