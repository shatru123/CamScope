using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CamScope.Core.Models;
using CamScope.Discovery.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class SocketInterfaceBinderTests
{
    [Fact]
    public void ResolveInterfaceIndex_WhenNull_ReturnsZero()
    {
        uint idx = SocketInterfaceBinder.ResolveInterfaceIndex(null!);
        Assert.Equal(0u, idx);
    }

    [Fact]
    public void ResolveInterfaceIndex_WhenInterfaceIndexSet_ReturnsPresetValue()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "custom0",
            InterfaceIndex = 42
        };

        uint idx = SocketInterfaceBinder.ResolveInterfaceIndex(iface);
        Assert.Equal(42u, idx);
    }

    [Fact]
    public void InspectBindingSupport_WhenNull_ReturnsNotStrictlyBound()
    {
        var status = SocketInterfaceBinder.InspectBindingSupport(null);

        Assert.NotNull(status);
        Assert.False(status.IsStrictlyBound);
        Assert.Equal("None", status.InterfaceName);
        Assert.NotNull(status.LimitationWarning);
        Assert.Contains("No interface selected", status.LimitationWarning);
    }

    [Fact]
    public void InspectBindingSupport_WithPresetIndex_ReportsOsAppropriateBinding()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "en0",
            InterfaceIndex = 11,
            IPv4Address = "192.168.1.50"
        };

        var status = SocketInterfaceBinder.InspectBindingSupport(iface);

        Assert.NotNull(status);
        Assert.True(status.IsStrictlyBound);
        Assert.Equal("en0", status.InterfaceName);
        Assert.Equal(11, status.InterfaceIndex);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Contains("macOS IP_BOUND_IF", status.BindingMethod);
            Assert.Null(status.LimitationWarning);
            Assert.Contains("Strict OS Egress Pinning Active", status.Description);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Contains("Linux SO_BINDTODEVICE", status.BindingMethod);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Contains("Windows IP_UNICAST_IF", status.BindingMethod);
        }
    }

    [Fact]
    public void InspectBindingSupport_WithUnknownInterface_ReportsFallbackWarning()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "nonexistent_dev_9999",
            InterfaceIndex = 0,
            IPv4Address = "192.168.99.99"
        };

        var status = SocketInterfaceBinder.InspectBindingSupport(iface);

        Assert.NotNull(status);
        Assert.False(status.IsStrictlyBound);
        Assert.NotNull(status.LimitationWarning);
        Assert.Contains("could not be resolved", status.LimitationWarning);
        Assert.Contains("App-Scoped Local IP Bound", status.Description);
    }

    [Fact]
    public void BindSocketToInterface_OnLoopback_BindsSocketSuccessfully()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        var loopbackIface = new NetworkInterfaceInfo
        {
            Name = "lo0",
            IPv4Address = "127.0.0.1",
            IsLoopback = true
        };

        var status = SocketInterfaceBinder.BindSocketToInterface(
            socket,
            loopbackIface,
            IPAddress.Loopback,
            isMulticast: false,
            NullLogger.Instance);

        Assert.NotNull(status);
        Assert.Equal("lo0", status.InterfaceName);
        Assert.Equal("127.0.0.1", status.LocalIp);
        Assert.True(socket.IsBound);
    }
}
