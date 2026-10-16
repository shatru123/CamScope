using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class NetworkInventoryServiceTests
{
    private const string SampleHardwarePorts = @"
Hardware Port: Ethernet Adapter (en3)
Device: en3
Ethernet Address: 32:be:0b:26:2a:be

Hardware Port: Ethernet Adapter (en4)
Device: en4
Ethernet Address: 32:be:0b:26:2a:bf

Hardware Port: Thunderbolt Bridge
Device: bridge0
Ethernet Address: 36:14:45:84:7a:80

Hardware Port: Wi-Fi
Device: en0
Ethernet Address: 68:5e:dd:3c:b3:3b

Hardware Port: Thunderbolt 1
Device: en1
Ethernet Address: 36:14:45:84:7a:80

VLAN Configurations
===================
";

    private const string SampleNetstatRoute = @"
Routing tables

Internet:
Destination        Gateway            Flags               Netif Expire
default            192.168.31.1       UGScg                 en0       
127                127.0.0.1          UCS                   lo0       
127.0.0.1          127.0.0.1          UH                    lo0       
192.168.31         link#11            UCS                   en0      !
192.168.31.1       d8:23:e0:c3:7:fc   UHLWIir               en0   1190
";

    private const string SampleAirPortProfile = @"
Wi-Fi:
      Interfaces:
        en0:
          Card Type: Wi-Fi  (0x14E4, 0x4388)
          Status: Connected
          Current Network Information:
            Office_Secure_5G:
              PHY Mode: 802.11ax
              Channel: 157 (5GHz, 80MHz)
              Country Code: IN
              Network Type: Infrastructure
              Security: WPA2 Personal
              Signal / Noise: -34 dBm / -92 dBm
              Transmit Rate: 1200
          Other Local Wi-Fi Networks:
            Guest_Camera_LAN:
              PHY Mode: 802.11a/n/ac
              Channel: 52 (5GHz, 80MHz)
              Network Type: Infrastructure
              Security: WPA2 Personal
              Signal / Noise: -58 dBm / -90 dBm
            Warehouse_IoT:
              PHY Mode: 802.11b/g/n
              Channel: 6 (2GHz, 20MHz)
              Network Type: Infrastructure
              Security: WPA2/WPA3 Personal
";

    [Fact]
    public void ParseHardwarePortsOutput_MapsDevicesToPortsAndMacs()
    {
        var result = NetworkInventoryService.ParseHardwarePortsOutput(SampleHardwarePorts);

        Assert.Equal(5, result.Count);
        Assert.True(result.ContainsKey("en0"));
        Assert.Equal("Wi-Fi", result["en0"].Port);
        Assert.Equal("68:5e:dd:3c:b3:3b", result["en0"].Mac);

        Assert.True(result.ContainsKey("en3"));
        Assert.Equal("Ethernet Adapter (en3)", result["en3"].Port);
        Assert.Equal("32:be:0b:26:2a:be", result["en3"].Mac);

        Assert.True(result.ContainsKey("bridge0"));
        Assert.Equal("Thunderbolt Bridge", result["bridge0"].Port);
    }

    [Fact]
    public void ParseDefaultRouteOutput_IdentifiesGatewayAndInterface()
    {
        var (gateway, iface) = NetworkInventoryService.ParseDefaultRouteOutput(SampleNetstatRoute);

        Assert.Equal("192.168.31.1", gateway);
        Assert.Equal("en0", iface);
    }

    [Fact]
    public void ParseAirPortOutput_ExtractsCurrentAndOtherNetworks()
    {
        var (currentSsid, networks) = NetworkInventoryService.ParseAirPortOutput(SampleAirPortProfile);

        Assert.Equal("Office_Secure_5G", currentSsid);
        Assert.Equal(3, networks.Count);

        var current = networks.First(n => n.IsCurrent);
        Assert.Equal("Office_Secure_5G", current.Ssid);
        Assert.Equal(-34, current.Rssi);
        Assert.Equal(157, current.Channel);
        Assert.Equal("5 GHz", current.Band);
        Assert.Equal("WPA2 Personal", current.Security);
        Assert.Equal("802.11ax", current.PhyMode);

        var guest = networks.First(n => n.Ssid == "Guest_Camera_LAN");
        Assert.False(guest.IsCurrent);
        Assert.Equal(-58, guest.Rssi);
        Assert.Equal("5 GHz", guest.Band);
        Assert.Equal(52, guest.Channel);

        var warehouse = networks.First(n => n.Ssid == "Warehouse_IoT");
        Assert.False(warehouse.IsCurrent);
        Assert.Null(warehouse.Rssi);
        Assert.False(warehouse.HasSignalStrength);
        Assert.Equal("Unknown (Not reported by OS)", warehouse.SignalText);
        Assert.Equal("2.4 GHz", warehouse.Band);
        Assert.Equal(6, warehouse.Channel);
    }

    [Theory]
    [InlineData("lo0", "", "Loopback", System.Net.NetworkInformation.NetworkInterfaceType.Loopback, true, NetworkInterfaceKind.Loopback)]
    [InlineData("en0", "Wi-Fi", "AirPort", System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, false, NetworkInterfaceKind.WiFi)]
    [InlineData("en3", "Ethernet Adapter (en3)", "USB 10/100/1000 LAN", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, false, NetworkInterfaceKind.UsbEthernet)]
    [InlineData("en1", "Thunderbolt 1", "Thunderbolt", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, false, NetworkInterfaceKind.Thunderbolt)]
    [InlineData("bridge0", "Thunderbolt Bridge", "Bridge", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, false, NetworkInterfaceKind.Virtual)]
    [InlineData("utun3", "", "VPN Tunnel", System.Net.NetworkInformation.NetworkInterfaceType.Tunnel, false, NetworkInterfaceKind.Vpn)]
    public void ClassifyInterfaceKind_ClassifiesAccurately(
        string name, string port, string desc, System.Net.NetworkInformation.NetworkInterfaceType type, bool isLoopback, NetworkInterfaceKind expected)
    {
        var kind = NetworkInventoryService.ClassifyInterfaceKind(name, port, desc, type, isLoopback);
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void VerifyRoutingInvariant_ReturnsTrueWhenUnchanged()
    {
        var service = new NetworkInventoryService(
            NullLogger<NetworkInventoryService>.Instance,
            new NetworkPermissionService());

        var before = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            DnsServers = ["1.1.1.1", "8.8.8.8"],
            CurrentWifiSsid = "Office_5G"
        };
        var after = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            DnsServers = ["8.8.8.8", "1.1.1.1"],
            CurrentWifiSsid = "Office_5G"
        };

        var result = service.VerifyRoutingInvariant(before, after);
        Assert.True(result.IsStable);
        Assert.True(result.VerificationSucceeded);
        Assert.Empty(result.MutationDetails);
        Assert.Contains("Confirmed Stable", result.StatusText);

        bool legacyOk = service.VerifyRoutingInvariant(before, after, out var warning);
        Assert.True(legacyOk);
        Assert.Null(warning);
    }

    [Fact]
    public void VerifyRoutingInvariant_DetectsMutatedDefaultGateway()
    {
        var service = new NetworkInventoryService(
            NullLogger<NetworkInventoryService>.Instance,
            new NetworkPermissionService());

        var before = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0"
        };
        var after = new NetworkRoutingSnapshot
        {
            DefaultGateway = "10.0.0.1",
            PrimaryInterfaceName = "en0"
        };

        var result = service.VerifyRoutingInvariant(before, after);
        Assert.False(result.IsStable);
        Assert.Single(result.MutationDetails);
        Assert.Contains("Default gateway changed", result.MutationDetails[0]);

        bool ok = service.VerifyRoutingInvariant(before, after, out var warning);
        Assert.False(ok);
        Assert.NotNull(warning);
        Assert.Contains("Default gateway changed", warning);
    }

    [Fact]
    public void VerifyRoutingInvariant_DetectsMutatedPrimaryInterface()
    {
        var service = new NetworkInventoryService(
            NullLogger<NetworkInventoryService>.Instance,
            new NetworkPermissionService());

        var before = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0"
        };
        var after = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en3"
        };

        var result = service.VerifyRoutingInvariant(before, after);
        Assert.False(result.IsStable);
        Assert.Contains(result.MutationDetails, m => m.Contains("Primary network interface changed"));
    }

    [Fact]
    public void VerifyRoutingInvariant_DetectsMutatedDnsServers()
    {
        var service = new NetworkInventoryService(
            NullLogger<NetworkInventoryService>.Instance,
            new NetworkPermissionService());

        var before = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            DnsServers = ["1.1.1.1"]
        };
        var after = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            DnsServers = ["9.9.9.9"]
        };

        var result = service.VerifyRoutingInvariant(before, after);
        Assert.False(result.IsStable);
        Assert.Contains(result.MutationDetails, m => m.Contains("DNS servers changed"));
    }

    [Fact]
    public void VerifyRoutingInvariant_DetectsMutatedWifiSsid()
    {
        var service = new NetworkInventoryService(
            NullLogger<NetworkInventoryService>.Instance,
            new NetworkPermissionService());

        var before = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            CurrentWifiSsid = "Corporate_WiFi"
        };
        var after = new NetworkRoutingSnapshot
        {
            DefaultGateway = "192.168.1.1",
            PrimaryInterfaceName = "en0",
            CurrentWifiSsid = "Rogue_AP"
        };

        var result = service.VerifyRoutingInvariant(before, after);
        Assert.False(result.IsStable);
        Assert.Contains(result.MutationDetails, m => m.Contains("Active Wi-Fi SSID changed"));
    }

    private const string SampleRedactedAirPortProfile = @"
Wi-Fi:
      Interfaces:
        en0:
          Card Type: Wi-Fi  (0x14E4, 0x4388)
          Status: Connected
          Current Network Information:
            <redacted>:
              PHY Mode: 802.11ax
              Channel: 157 (5GHz, 80MHz)
              Security: WPA2 Personal
              Signal / Noise: -43 dBm / -93 dBm
          Other Local Wi-Fi Networks:
            <redacted>:
              PHY Mode: 802.11b/g
              Channel: 11 (2GHz, 20MHz)
              Security: WPA2 Personal
            <redacted>:
              PHY Mode: 802.11a/n/ac/ax
              Channel: 60 (5GHz, 160MHz)
              Security: WPA2 Personal
            Valid_Unredacted_Net:
              PHY Mode: 802.11ax
              Channel: 36 (5GHz, 80MHz)
              Security: WPA2 Personal
              Signal / Noise: -55 dBm / -90 dBm
";

    [Fact]
    public void ParseAirPortOutput_FiltersOutRedactedSSIDs()
    {
        var (currentSsid, networks) = NetworkInventoryService.ParseAirPortOutput(SampleRedactedAirPortProfile);

        Assert.Null(currentSsid);
        Assert.Single(networks);
        Assert.Equal("Valid_Unredacted_Net", networks[0].Ssid);
        Assert.DoesNotContain(networks, n => n.Ssid.Equals("<redacted>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MacCoreWlanScanner_ParseScanJson_ParsesUnredactedNetworksAndFiltersRedacted()
    {
        string sampleJson = @"
[
  {
    ""ssid"": ""ShatruNeha"",
    ""rssi"": -40,
    ""channel"": 157,
    ""band"": ""5 GHz"",
    ""security"": ""WPA2 Personal"",
    ""bssid"": ""aa:bb:cc:dd:ee:ff""
  },
  {
    ""ssid"": ""<redacted>"",
    ""rssi"": -70,
    ""channel"": 6,
    ""band"": ""2.4 GHz"",
    ""security"": ""WPA2 Personal"",
    ""bssid"": null
  },
  {
    ""ssid"": ""Airtel_moha_3777"",
    ""rssi"": -68,
    ""channel"": 11,
    ""band"": ""2.4 GHz"",
    ""security"": ""WPA2 Personal"",
    ""bssid"": null
  }
]";

        var networks = MacCoreWlanScanner.ParseScanJson(sampleJson);

        Assert.Equal(2, networks.Count);
        Assert.Equal("ShatruNeha", networks[0].Ssid);
        Assert.Equal(-40, networks[0].Rssi);
        Assert.Equal(157, networks[0].Channel);
        Assert.Equal("5 GHz", networks[0].Band);
        Assert.Equal("WPA2 Personal", networks[0].Security);

        Assert.Equal("Airtel_moha_3777", networks[1].Ssid);
        Assert.Equal(-68, networks[1].Rssi);
        Assert.Equal(11, networks[1].Channel);
        Assert.Equal("2.4 GHz", networks[1].Band);

        Assert.DoesNotContain(networks, n => n.Ssid.Equals("<redacted>", StringComparison.OrdinalIgnoreCase));
    }
}
