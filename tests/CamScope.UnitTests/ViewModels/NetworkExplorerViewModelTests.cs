using CamScope.App.ViewModels;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class NetworkExplorerViewModelTests
{
    private sealed class FakeNetworkInventoryService : INetworkInventoryService
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
                    IPv4Address = "192.168.31.226",
                    SubnetMask = "255.255.255.0",
                    GatewayAddress = "192.168.31.1",
                    IsUp = true,
                    SupportsMulticast = true,
                    IsPrimaryInternet = true,
                    InterfaceKind = NetworkInterfaceKind.WiFi
                },
                new NetworkInterfaceInfo
                {
                    Id = "en3",
                    Name = "en3",
                    HardwarePort = "Ethernet Adapter (en3)",
                    IPv4Address = "10.0.0.5",
                    SubnetMask = "255.255.255.0",
                    GatewayAddress = null,
                    IsUp = true,
                    SupportsMulticast = true,
                    IsPrimaryInternet = false,
                    InterfaceKind = NetworkInterfaceKind.UsbEthernet
                }
            ],
            PrimaryInterface = new NetworkInterfaceInfo
            {
                Id = "en0",
                Name = "en0",
                HardwarePort = "Wi-Fi",
                IPv4Address = "192.168.31.226",
                IsPrimaryInternet = true
            },
            WifiNetworks =
            [
                new WifiNetworkInfo
                {
                    Ssid = "Office_5G",
                    Rssi = -42,
                    Channel = 157,
                    Band = "5 GHz",
                    IsCurrent = true
                },
                new WifiNetworkInfo
                {
                    Ssid = "Camera_Lab",
                    Rssi = null,
                    Channel = 6,
                    Band = "2.4 GHz",
                    IsCurrent = false
                }
            ],
            CurrentWifiSsid = "Office_5G",
            SystemDefaultGateway = "192.168.31.1",
            SystemDnsServers = ["192.168.31.1", "1.1.1.1"],
            IsWifiPermitted = true
        };

        public RoutingInvariantResult InvariantToReturn { get; set; } = new()
        {
            IsStable = true,
            VerificationSucceeded = true,
            StatusText = "Primary routing table invariant confirmed stable"
        };

        public Task<NetworkInventoryReport> GetNetworkInventoryAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(Report);

        public Task<NetworkRoutingSnapshot> CaptureRoutingSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new NetworkRoutingSnapshot
            {
                DefaultGateway = Report.SystemDefaultGateway,
                PrimaryInterfaceName = "en0"
            });

        public RoutingInvariantResult VerifyRoutingInvariant(NetworkRoutingSnapshot? before, NetworkRoutingSnapshot? after)
            => InvariantToReturn;

        public bool VerifyRoutingInvariant(NetworkRoutingSnapshot before, NetworkRoutingSnapshot after, out string? mutationWarning)
        {
            mutationWarning = InvariantToReturn.IsStable ? null : string.Join("; ", InvariantToReturn.MutationDetails);
            return InvariantToReturn.IsStable;
        }
    }

    private sealed class FakeSubnetScanner : ISubnetScanner
    {
        public List<CameraDevice> DevicesToReturn { get; set; } = [];

        public Task<IReadOnlyList<CameraDevice>> ScanSubnetAsync(
            SubnetScanRequest request,
            IProgress<DiscoveryProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new DiscoveryProgressReport
            {
                Status = DiscoveryStatus.Completed,
                Percentage = 100,
                CurrentStep = "Completed",
                DevicesFound = DevicesToReturn.Count
            });
            return Task.FromResult<IReadOnlyList<CameraDevice>>(DevicesToReturn);
        }
    }

    private sealed class FakeDeviceRepository : IDeviceRepository
    {
        public List<CameraDevice> Stored { get; } = [];

        public Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CameraDevice>>(Stored);

        public Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Stored.FirstOrDefault(d => d.Id == id));

        public Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default)
            => Task.FromResult(Stored.FirstOrDefault(d => d.EndpointId == endpointId));

        public Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default)
        {
            var idx = Stored.FindIndex(d => d.EndpointId == device.EndpointId);
            if (idx >= 0) Stored[idx] = device;
            else Stored.Add(device);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Stored.RemoveAll(d => d.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken ct = default)
        {
            Stored.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public List<ActivityAuditEntry> Entries { get; } = [];

        public Task LogAsync(AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = "LocalUser", CancellationToken ct = default)
        {
            Entries.Add(new ActivityAuditEntry
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
            => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>(Entries);

        public Task ClearLogsAsync(CancellationToken ct = default)
        {
            Entries.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RefreshInventory_PopulatesAdaptersAndWifiTelemetry()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Interfaces.Count);
        Assert.Equal(2, vm.WifiNetworks.Count);
        Assert.Equal("192.168.31.1", vm.DefaultGatewayText);
        Assert.Equal("Office_5G", vm.CurrentWifiText);
        Assert.NotNull(vm.SelectedScanningInterface);
        Assert.Equal("en0", vm.SelectedScanningInterface.Name);
    }

    [Fact]
    public async Task SelectInterface_UpdatesSelectedAndSelectionService()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        var secondary = vm.Interfaces.First(i => i.Name == "en3");
        vm.SelectInterface(secondary);

        Assert.Equal("en3", vm.SelectedScanningInterface?.Name);
        Assert.Equal("en3", selectionService.CurrentScanningInterface?.Name);
        Assert.Equal("10.0.0.0/24", vm.SubnetInput);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.NetworkInterfaceSelected);
    }

    [Fact]
    public void EvaluateReachability_ForPrimaryInterface_HasInfoSeverity()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "en0",
            HardwarePort = "Wi-Fi",
            IPv4Address = "192.168.31.50",
            GatewayAddress = "192.168.31.1",
            IsUp = true,
            SupportsMulticast = true,
            IsPrimaryInternet = true
        };

        var diag = NetworkExplorerViewModel.EvaluateReachability(iface, "192.168.31.1");

        Assert.Equal(DiagnosticSeverity.Info, diag.Severity);
        Assert.True(diag.IsMulticastSupported);
        Assert.True(diag.IsSubnetRoutable);
        Assert.True(diag.HasDefaultGateway);
        Assert.Contains("primary internet connection", diag.SummaryExplanation);
    }

    [Fact]
    public void EvaluateReachability_ForLoopback_HasWarningSeverity()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "lo0",
            IPv4Address = "127.0.0.1",
            IsUp = true,
            IsLoopback = true
        };

        var diag = NetworkExplorerViewModel.EvaluateReachability(iface, null);

        Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
        Assert.Contains("Loopback adapter", diag.SummaryExplanation);
    }

    [Fact]
    public void EvaluateReachability_ForDownInterface_HasErrorSeverity()
    {
        var iface = new NetworkInterfaceInfo
        {
            Name = "en4",
            IPv4Address = "10.0.0.1",
            IsUp = false
        };

        var diag = NetworkExplorerViewModel.EvaluateReachability(iface, null);

        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("down", diag.SummaryExplanation);
    }

    [Fact]
    public void WifiModal_OpensAndClosesCorrectly()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "Camera_Lab" };
        vm.OpenWifiModal(targetWifi);

        Assert.True(vm.IsWifiModalOpen);
        Assert.Equal("Camera_Lab", vm.ModalTargetSsid);

        vm.CloseWifiModal();
        Assert.False(vm.IsWifiModalOpen);
    }

    [Fact]
    public async Task StartSubnetScan_ExecutesAndStoresDiscoveredCameras()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner
        {
            DevicesToReturn =
            [
                new CameraDevice
                {
                    EndpointId = "urn:uuid:cam-101",
                    DisplayName = "Subnet Axis Camera",
                    Host = "10.0.0.22",
                    Port = 80,
                    Vendor = "Axis"
                }
            ]
        };
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        vm.SubnetInput = "10.0.0.0/24";
        await vm.StartSubnetScanCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.SubnetDiscoveredCount);
        Assert.Single(vm.SubnetDiscoveredDevices);
        Assert.Single(repo.Stored);
        Assert.Equal("Subnet Axis Camera", repo.Stored[0].DisplayName);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.SubnetScanStarted);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.SubnetScanCompleted);
    }

    [Fact]
    public async Task StartSubnetScan_WhenCidrEmpty_SetsErrorMessage()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        vm.SubnetInput = "";
        await vm.StartSubnetScanCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SubnetErrorMessage);
        Assert.Contains("valid CIDR", vm.SubnetErrorMessage);
    }

    [Fact]
    public async Task RefreshInventory_InitializesBindingStatusAndAdapterTopology()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        Assert.True(vm.HasMultiplePhysicalAdapters);
        Assert.Contains("Multiple active network adapters detected", vm.AdapterTopologyExplanation);
        Assert.NotNull(vm.BindingStatus);
        Assert.NotEmpty(vm.BindingStatusText);
        Assert.True(vm.IsRoutingStable);
        Assert.Contains("Protected", vm.RoutingStabilityText);
    }

    [Fact]
    public async Task WifiNetworks_WithNullRssi_ShowsUnknownSignalText()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        var unknownWifi = vm.WifiNetworks.First(w => w.Ssid == "Camera_Lab");
        Assert.Null(unknownWifi.Rssi);
        Assert.False(unknownWifi.HasSignalStrength);
        Assert.Equal("Unknown (Not reported by OS)", unknownWifi.SignalText);
    }

    [Fact]
    public async Task StartSubnetScan_WhenRoutingMutated_DisplaysWarningAndSetsStabilityFalse()
    {
        var inventoryService = new FakeNetworkInventoryService();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var scanner = new FakeSubnetScanner();
        var repo = new FakeDeviceRepository();
        var audit = new FakeAuditService();

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            scanner,
            repo,
            audit,
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        // Simulate a routing mutation reported by inventory service
        inventoryService.InvariantToReturn = new RoutingInvariantResult
        {
            IsStable = false,
            VerificationSucceeded = true,
            StatusText = "WARNING: Primary routing mutation detected: Default gateway mutated",
            MutationDetails = ["Default gateway mutated from 192.168.31.1 to 10.0.0.1"]
        };

        await vm.StartSubnetScanCommand.ExecuteAsync(null);

        Assert.False(vm.IsRoutingStable);
        Assert.Contains("WARNING: Primary routing mutation detected", vm.RoutingStabilityText);
        Assert.NotNull(vm.SubnetErrorMessage);
        Assert.Contains("Routing warning", vm.SubnetErrorMessage);
    }
}
