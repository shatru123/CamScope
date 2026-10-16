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
}
