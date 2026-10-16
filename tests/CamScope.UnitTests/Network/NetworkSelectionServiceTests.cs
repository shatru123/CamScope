using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class NetworkSelectionServiceTests
{
    [Fact]
    public void SelectScanningInterface_UpdatesCurrentAndRaisesEvent()
    {
        var service = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        NetworkInterfaceInfo? received = null;
        int eventCount = 0;

        service.SelectionChanged += iface =>
        {
            received = iface;
            eventCount++;
        };

        var target = new NetworkInterfaceInfo
        {
            Id = "en3",
            Name = "en3",
            IPv4Address = "192.168.2.100",
            HardwarePort = "USB Ethernet"
        };

        service.SelectScanningInterface(target);

        Assert.NotNull(service.CurrentScanningInterface);
        Assert.Equal("en3", service.CurrentScanningInterface.Name);
        Assert.Equal("192.168.2.100", service.CurrentScanningInterface.IPv4Address);
        Assert.Equal(1, eventCount);
        Assert.Same(target, received);
    }

    [Fact]
    public void SelectScanningInterface_DoesNotDuplicateEventWhenIdentical()
    {
        var service = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        int eventCount = 0;
        service.SelectionChanged += _ => eventCount++;

        var target = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            IPv4Address = "192.168.1.50"
        };

        service.SelectScanningInterface(target);
        service.SelectScanningInterface(target);

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void SelectScanningInterface_ThrowsOnNull()
    {
        var service = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        Assert.Throws<ArgumentNullException>(() => service.SelectScanningInterface(null!));
    }

    [Fact]
    public void SetCctvInterface_UpdatesCctvPropertiesAndRaisesEvent()
    {
        var service = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        NetworkInterfaceInfo? received = null;
        int eventCount = 0;

        service.CctvInterfaceChanged += iface =>
        {
            received = iface;
            eventCount++;
        };

        var cctvIface = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi (Secondary)",
            IPv4Address = "192.168.10.5"
        };
        var connection = new WifiConnectionInfo
        {
            Ssid = "CCTV_Secure_5G",
            InterfaceName = "en1",
            IPv4Address = "192.168.10.5",
            IsCctvNetwork = true
        };

        service.SetCctvInterface(cctvIface, connection);

        Assert.NotNull(service.CctvInterface);
        Assert.Equal("en1", service.CctvInterface.Name);
        Assert.NotNull(service.CurrentCctvWifiConnection);
        Assert.Equal("CCTV_Secure_5G", service.CurrentCctvWifiConnection.Ssid);
        Assert.Equal(1, eventCount);
        Assert.Same(cctvIface, received);
    }

    [Fact]
    public void SetCctvInterface_CanClearCctvInterface()
    {
        var service = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var cctvIface = new NetworkInterfaceInfo { Id = "en1", Name = "en1" };
        service.SetCctvInterface(cctvIface);

        int eventCount = 0;
        service.CctvInterfaceChanged += iface =>
        {
            if (iface == null) eventCount++;
        };

        service.SetCctvInterface(null, null);

        Assert.Null(service.CctvInterface);
        Assert.Null(service.CurrentCctvWifiConnection);
        Assert.Equal(1, eventCount);
    }
}
