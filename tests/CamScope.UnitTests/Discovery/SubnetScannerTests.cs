using System.Net;
using CamScope.Core.Models;
using CamScope.Discovery.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Discovery;

public class SubnetScannerTests
{
    [Fact]
    public void CalculateHostIps_Slash24_Returns254Hosts()
    {
        var hosts = SubnetScanner.CalculateHostIps("192.168.1.0/24");

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
    }

    [Fact]
    public void CalculateHostIps_Slash28_Returns14Hosts()
    {
        var hosts = SubnetScanner.CalculateHostIps("10.0.0.0/28");

        Assert.Equal(14, hosts.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("10.0.0.14"), hosts[^1]);
    }

    [Fact]
    public void CalculateHostIps_Slash32_Returns1Host()
    {
        var hosts = SubnetScanner.CalculateHostIps("192.168.1.55/32");

        Assert.Single(hosts);
        Assert.Equal(IPAddress.Parse("192.168.1.55"), hosts[0]);
    }

    [Fact]
    public void CalculateHostIps_SingleIpNoSlash_Returns1Host()
    {
        var hosts = SubnetScanner.CalculateHostIps("192.168.1.99");

        Assert.Single(hosts);
        Assert.Equal(IPAddress.Parse("192.168.1.99"), hosts[0]);
    }

    [Fact]
    public void CalculateHostIps_CapsAtMaxHosts()
    {
        // /16 would normally have 65534 hosts; verify capped to 50
        var hosts = SubnetScanner.CalculateHostIps("10.0.0.0/16", maxHosts: 50);

        Assert.Equal(50, hosts.Count);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), hosts[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-ip")]
    [InlineData("192.168.1.0/35")]
    [InlineData("192.168.1.0/-1")]
    [InlineData("999.999.999.999/24")]
    public void CalculateHostIps_InvalidInput_ThrowsArgumentException(string invalidInput)
    {
        Assert.Throws<ArgumentException>(() => SubnetScanner.CalculateHostIps(invalidInput));
    }

    [Fact]
    public async Task ScanSubnetAsync_ThrowsIfInterfaceMissingIp()
    {
        var scanner = new SubnetScanner(NullLogger<SubnetScanner>.Instance);
        var request = new SubnetScanRequest
        {
            TargetInterface = new NetworkInterfaceInfo { Name = "test", IPv4Address = "" },
            CidrOrSubnet = "192.168.1.0/24"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanSubnetAsync(request));
    }

    [Fact]
    public async Task ScanSubnetAsync_HonorsCancellation()
    {
        var scanner = new SubnetScanner(NullLogger<SubnetScanner>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var request = new SubnetScanRequest
        {
            TargetInterface = new NetworkInterfaceInfo
            {
                Name = "lo0",
                IPv4Address = "127.0.0.1",
                IsLoopback = true
            },
            CidrOrSubnet = "127.0.0.1/32",
            TimeoutPerHost = TimeSpan.FromMilliseconds(50)
        };

        var results = await scanner.ScanSubnetAsync(request, ct: cts.Token);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task ScanSubnetAsync_OverlappingSubnetInterface_BindsCorrectlyWithoutThrowing()
    {
        var scanner = new SubnetScanner(NullLogger<SubnetScanner>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Simulate an adapter configured with overlapping subnet
        var request = new SubnetScanRequest
        {
            TargetInterface = new NetworkInterfaceInfo
            {
                Name = "en3",
                InterfaceIndex = 12,
                IPv4Address = "192.168.1.150"
            },
            CidrOrSubnet = "192.168.1.150/32",
            TimeoutPerHost = TimeSpan.FromMilliseconds(50),
            MaxConcurrency = 1
        };

        var results = await scanner.ScanSubnetAsync(request, ct: cts.Token);
        Assert.NotNull(results);
    }
}
