using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class WifiNetworkManagerTests
{
    private sealed class FakeInventoryService : INetworkInventoryService
    {
        public NetworkInventoryReport Report { get; set; } = new()
        {
            Interfaces =
            [
                new NetworkInterfaceInfo
                {
                    Id = "en0",
                    Name = "en0",
                    HardwarePort = "Wi-Fi",
                    IPv4Address = "192.168.1.100",
                    IsUp = true,
                    IsPrimaryInternet = true
                }
            ],
            PrimaryInterface = new NetworkInterfaceInfo
            {
                Id = "en0",
                Name = "en0",
                HardwarePort = "Wi-Fi",
                IPv4Address = "192.168.1.100",
                IsPrimaryInternet = true
            },
            WifiNetworks =
            [
                new WifiNetworkInfo
                {
                    Ssid = "Test_Office_5G",
                    Rssi = -55,
                    Channel = 36,
                    Band = "5 GHz",
                    Security = "WPA2 Personal"
                }
            ],
            SystemDefaultGateway = "192.168.1.1",
            SystemDnsServers = ["192.168.1.1"]
        };

        public Task<NetworkInventoryReport> GetNetworkInventoryAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(Report);

        public Task<NetworkRoutingSnapshot> CaptureRoutingSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new NetworkRoutingSnapshot
            {
                DefaultGateway = Report.SystemDefaultGateway,
                PrimaryInterfaceName = "en0",
                DnsServers = Report.SystemDnsServers
            });

        public RoutingInvariantResult VerifyRoutingInvariant(NetworkRoutingSnapshot? before, NetworkRoutingSnapshot? after)
            => new() { IsStable = true, VerificationSucceeded = true };

        public bool VerifyRoutingInvariant(NetworkRoutingSnapshot before, NetworkRoutingSnapshot after, out string? mutationWarning)
        {
            mutationWarning = null;
            return true;
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public List<ActivityAuditEntry> LoggedEntries { get; } = [];

        public Task LogAsync(CamScope.Core.Enums.AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = "LocalUser", CancellationToken ct = default)
        {
            LoggedEntries.Add(new ActivityAuditEntry
            {
                Action = action,
                TargetEndpoint = targetEndpoint,
                Succeeded = succeeded,
                Details = details,
                UserOrSource = userOrSource
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int maxCount = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>(LoggedEntries);

        public Task ClearLogsAsync(CancellationToken ct = default)
        {
            LoggedEntries.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MacWifiNetworkManager_ThrowsOnNullRequest()
    {
        var inventory = new FakeInventoryService();
        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.ConnectAsync(null!));
    }

    [Fact]
    public async Task MacWifiNetworkManager_RejectsEmptySsid()
    {
        var inventory = new FakeInventoryService();
        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "  ",
            InterfaceName = "en0"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be empty", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MacWifiNetworkManager_WarnsOnSingleAdapterWithoutOperatorConfirmation()
    {
        var inventory = new FakeInventoryService();
        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance);

        // If host has single adapter or CanSupportConcurrentConnections is false
        if (!manager.CanSupportConcurrentConnections)
        {
            var result = await manager.ConnectAsync(new WifiConnectRequest
            {
                Ssid = "Cam_AP",
                Password = "SecretPassword123",
                InterfaceName = "en0",
                AllowSwitchingPrimaryIfSingleAdapter = false
            });

            Assert.False(result.Succeeded);
            Assert.Contains("switch your primary internet connection", result.Message);
            // Verify password was NEVER leaked in the message
            Assert.DoesNotContain("SecretPassword123", result.Message);
        }
    }

    [Fact]
    public async Task CrossPlatformManagers_HandleUnsupportedGracefully()
    {
        var linuxManager = new LinuxWifiNetworkManager(NullLogger<LinuxWifiNetworkManager>.Instance);
        var winManager = new WindowsWifiNetworkManager(NullLogger<WindowsWifiNetworkManager>.Instance);
        var unsupported = new UnsupportedPlatformWifiNetworkManager();

        var linuxResult = await linuxManager.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(linuxResult.Succeeded);

        var winResult = await winManager.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(winResult.Succeeded);

        var unsupResult = await unsupported.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(unsupResult.Succeeded);
    }

    [Fact]
    public void WifiNetworkInfo_EvaluatesSecurityAndBandCorrectly()
    {
        var openNet = new WifiNetworkInfo
        {
            Ssid = "Guest_Open",
            Security = "None",
            Band = "2.4 GHz",
            Channel = 1
        };
        Assert.False(openNet.IsSecured);
        Assert.Equal("2.4 GHz", openNet.BandGroup);

        var securedNet = new WifiNetworkInfo
        {
            Ssid = "Secure_5G",
            Security = "WPA3 Personal",
            Band = "5 GHz",
            Channel = 149
        };
        Assert.True(securedNet.IsSecured);
        Assert.Equal("5 GHz", securedNet.BandGroup);
    }
}
