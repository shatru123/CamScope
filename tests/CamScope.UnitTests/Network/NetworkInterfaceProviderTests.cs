using System.Net;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class NetworkInterfaceProviderTests
{
    private const string SampleMacIfconfigOutput = @"lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> mtu 16384
	options=1203<RXCSUM,TXCSUM,TXSTATUS,SW_TIMESTAMP>
	inet 127.0.0.1 netmask 0xff000000 
	inet6 ::1 prefixlen 128 
en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500
	options=6463<RXCSUM,TXCSUM,TSO4,TSO6,CHANNEL_IO,PARTIAL_CSUM,ZEROCOPY_RX>
	ether a4:83:e7:12:34:56 
	inet 192.168.1.150 netmask 0xffffff00 broadcast 192.168.1.255
en5: flags=8823<UP,BROADCAST,RUNNING> mtu 1500
	ether 00:e0:4c:68:01:23 
	inet 10.20.30.40 netmask 0xffff0000 broadcast 10.20.255.255
utun0: flags=8011<UP,POINTOPOINT,MULTICAST> mtu 1380
	inet 172.16.0.2 --> 172.16.0.1 netmask 0xffffff00 
en9: flags=8862<BROADCAST,SMART,RUNNING> mtu 1500
	inet 192.168.99.1 netmask 0xffffff00
";

    [Fact]
    public void ParseIfconfig_ExcludesLoopbackByDefault()
    {
        var interfaces = NetworkInterfaceProvider.ParseIfconfigOutput(SampleMacIfconfigOutput, includeLoopback: false);

        Assert.DoesNotContain(interfaces, i => i.Name == "lo0");
        Assert.DoesNotContain(interfaces, i => i.IPv4Address.StartsWith("127."));
        Assert.Contains(interfaces, i => i.Name == "en0");
    }

    [Fact]
    public void ParseIfconfig_IncludesLoopbackWhenRequested()
    {
        var interfaces = NetworkInterfaceProvider.ParseIfconfigOutput(SampleMacIfconfigOutput, includeLoopback: true);

        Assert.Contains(interfaces, i => i.Name == "lo0");
        var lo = interfaces.First(i => i.Name == "lo0");
        Assert.True(lo.IsLoopback);
        Assert.Equal("127.0.0.1", lo.IPv4Address);
        Assert.Equal("255.0.0.0", lo.SubnetMask);
    }

    [Fact]
    public void ParseIfconfig_IdentifiesMulticastSupportAccurately()
    {
        var interfaces = NetworkInterfaceProvider.ParseIfconfigOutput(SampleMacIfconfigOutput, includeLoopback: false);

        var en0 = interfaces.First(i => i.Name == "en0");
        Assert.True(en0.SupportsMulticast, "en0 has MULTICAST flag and must report true");

        var en5 = interfaces.First(i => i.Name == "en5");
        Assert.False(en5.SupportsMulticast, "en5 lacks MULTICAST flag and must report false");
    }

    [Fact]
    public void ParseIfconfig_FiltersDownInterfaces()
    {
        var interfaces = NetworkInterfaceProvider.ParseIfconfigOutput(SampleMacIfconfigOutput, includeLoopback: false);

        Assert.DoesNotContain(interfaces, i => i.Name == "en9");
    }

    [Fact]
    public void ParseIfconfig_HandlesEmptyAndMalformedOutput()
    {
        var empty = NetworkInterfaceProvider.ParseIfconfigOutput("");
        Assert.Empty(empty);

        var whitespace = NetworkInterfaceProvider.ParseIfconfigOutput("   \n\t   ");
        Assert.Empty(whitespace);

        var malformed = NetworkInterfaceProvider.ParseIfconfigOutput("Garbage text without any valid interface syntax\nflags=none inet 999.999.999");
        Assert.Empty(malformed);
    }

    [Theory]
    [InlineData("0xffffff00", "255.255.255.0")]
    [InlineData("0xffff0000", "255.255.0.0")]
    [InlineData("0xff000000", "255.0.0.0")]
    [InlineData("0xfffffffc", "255.255.255.252")]
    [InlineData("255.255.255.0", "255.255.255.0")]
    [InlineData("", "255.255.255.0")]
    [InlineData("invalid", "255.255.255.0")]
    public void NormalizeNetmask_ConvertsCorrectly(string raw, string expected)
    {
        var actual = NetworkInterfaceProvider.NormalizeNetmask(raw);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Provider_WithLoopbackAllowed_ReturnsAtLeastOneInterface()
    {
        var provider = new NetworkInterfaceProvider(NullLogger<NetworkInterfaceProvider>.Instance);
        var ifaces = provider.GetEligibleInterfaces(includeLoopback: true);

        Assert.NotEmpty(ifaces);
    }
}
