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

    private sealed class FakeWifiNetworkManager : IWifiNetworkManager
    {
        public bool IsPlatformSupported { get; set; } = true;
        public bool CanSupportConcurrentConnections { get; set; } = false;
        public List<WifiNetworkInfo> AvailableNetworks { get; set; } = [];
        public List<WifiConnectionInfo> CurrentConnections { get; set; } = [];
        public List<NetworkInterfaceInfo> WifiInterfaces { get; set; } = [];
        public WifiConnectionResult ConnectResult { get; set; } = new()
        {
            Succeeded = true,
            ConnectedInterface = "en1",
            AssignedIp = "192.168.10.55",
            PreservedPrimaryConnection = true,
            Message = "Connected successfully"
        };
        public bool DisconnectResult { get; set; } = true;
        public WifiConnectionStatus StatusToReturn { get; set; } = WifiConnectionStatus.Connected;

        public WifiConnectRequest? LastConnectRequest { get; private set; }
        public string? LastDisconnectedInterface { get; private set; }

        public Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WifiNetworkInfo>>(AvailableNetworks);

        public Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WifiConnectionInfo>>(CurrentConnections);

        public Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NetworkInterfaceInfo>>(WifiInterfaces);

        public Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default)
        {
            LastConnectRequest = request;
            return Task.FromResult(ConnectResult);
        }

        public Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default)
        {
            LastDisconnectedInterface = interfaceName;
            return Task.FromResult(DisconnectResult);
        }

        public Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default)
            => Task.FromResult(StatusToReturn);
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
    public async Task StartSubnetScan_WhenInterfaceMissingIp_SetsErrorMessage()
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

        // Select an interface without an assigned IPv4 address
        vm.SelectedScanningInterface = new NetworkInterfaceInfo
        {
            Name = "en1",
            HardwarePort = "Thunderbolt 1",
            IPv4Address = ""
        };

        await vm.StartSubnetScanCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SubnetErrorMessage);
        Assert.Contains("does not have an active IPv4 address", vm.SubnetErrorMessage);
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

    [Fact]
    public async Task WifiFiltering_BySearchQuery_MatchesSsidAndBssid()
    {
        var inventory = new FakeNetworkInventoryService();
        inventory.Report.WifiNetworks =
        [
            new WifiNetworkInfo { Ssid = "Office_Secure_5G", Bssid = "00:11:22:33:44:55", Band = "5 GHz", Security = "WPA2" },
            new WifiNetworkInfo { Ssid = "Guest_Open", Bssid = "AA:BB:CC:DD:EE:FF", Band = "2.4 GHz", Security = "None" },
            new WifiNetworkInfo { Ssid = "CCTV_Lab_Network", Bssid = "11:22:33:44:55:66", Band = "5 GHz", Security = "WPA3" }
        ];

        var vm = new NetworkExplorerViewModel(
            inventory,
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.FilteredWifiNetworks.Count);

        // Search by partial SSID
        vm.WifiSearchQuery = "cctv";
        Assert.Single(vm.FilteredWifiNetworks);
        Assert.Equal("CCTV_Lab_Network", vm.FilteredWifiNetworks[0].Ssid);

        // Search by BSSID
        vm.WifiSearchQuery = "aa:bb";
        Assert.Single(vm.FilteredWifiNetworks);
        Assert.Equal("Guest_Open", vm.FilteredWifiNetworks[0].Ssid);

        // Clear search
        vm.WifiSearchQuery = "";
        Assert.Equal(3, vm.FilteredWifiNetworks.Count);
    }

    [Fact]
    public async Task WifiFiltering_BySecurity_FiltersCorrectly()
    {
        var inventory = new FakeNetworkInventoryService();
        inventory.Report.WifiNetworks =
        [
            new WifiNetworkInfo { Ssid = "Secured_1", Security = "WPA2" },
            new WifiNetworkInfo { Ssid = "Open_1", Security = "None" },
            new WifiNetworkInfo { Ssid = "Secured_2", Security = "WPA3" }
        ];

        var vm = new NetworkExplorerViewModel(
            inventory,
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        vm.SelectedSecurityFilter = "Secured";
        Assert.Equal(2, vm.FilteredWifiNetworks.Count);
        Assert.All(vm.FilteredWifiNetworks, w => Assert.True(w.IsSecured));

        vm.SelectedSecurityFilter = "Open";
        Assert.Single(vm.FilteredWifiNetworks);
        Assert.Equal("Open_1", vm.FilteredWifiNetworks[0].Ssid);

        vm.SelectedSecurityFilter = "All";
        Assert.Equal(3, vm.FilteredWifiNetworks.Count);
    }

    [Fact]
    public async Task WifiFiltering_ByBand_FiltersCorrectly()
    {
        var inventory = new FakeNetworkInventoryService();
        inventory.Report.WifiNetworks =
        [
            new WifiNetworkInfo { Ssid = "Fast_5G", Band = "5 GHz" },
            new WifiNetworkInfo { Ssid = "Legacy_24G", Band = "2.4 GHz" },
            new WifiNetworkInfo { Ssid = "Another_5G", Band = "5 GHz" }
        ];

        var vm = new NetworkExplorerViewModel(
            inventory,
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        vm.SelectedBandFilter = "5 GHz";
        Assert.Equal(2, vm.FilteredWifiNetworks.Count);
        Assert.All(vm.FilteredWifiNetworks, w => Assert.Equal("5 GHz", w.BandGroup));

        vm.SelectedBandFilter = "2.4 GHz";
        Assert.Single(vm.FilteredWifiNetworks);
        Assert.Equal("Legacy_24G", vm.FilteredWifiNetworks[0].Ssid);
    }

    [Fact]
    public async Task WifiSorting_SortsCorrectly()
    {
        var inventory = new FakeNetworkInventoryService();
        inventory.Report.WifiNetworks =
        [
            new WifiNetworkInfo { Ssid = "Zeta_Net", Rssi = -30, Channel = 1 },
            new WifiNetworkInfo { Ssid = "Alpha_Net", Rssi = -80, Channel = 11 },
            new WifiNetworkInfo { Ssid = "Beta_Net", Rssi = -50, Channel = 6 }
        ];

        var vm = new NetworkExplorerViewModel(
            inventory,
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        await vm.RefreshInventoryCommand.ExecuteAsync(null);

        // Sort by SSID
        vm.SelectedSortOption = "SSID (A-Z)";
        Assert.Equal("Alpha_Net", vm.FilteredWifiNetworks[0].Ssid);
        Assert.Equal("Beta_Net", vm.FilteredWifiNetworks[1].Ssid);
        Assert.Equal("Zeta_Net", vm.FilteredWifiNetworks[2].Ssid);

        // Sort by Channel
        vm.SelectedSortOption = "Channel";
        Assert.Equal(1, vm.FilteredWifiNetworks[0].Channel);
        Assert.Equal(6, vm.FilteredWifiNetworks[1].Channel);
        Assert.Equal(11, vm.FilteredWifiNetworks[2].Channel);

        // Sort by Signal Strength
        vm.SelectedSortOption = "Signal Strength";
        Assert.Equal("Zeta_Net", vm.FilteredWifiNetworks[0].Ssid);
    }

    [Fact]
    public void WifiModal_ShowHidePassword_TogglesMaskCharAndText()
    {
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        Assert.False(vm.IsPasswordVisible);
        Assert.Equal('•', vm.PasswordMaskChar);
        Assert.Equal("Show", vm.PasswordToggleText);

        vm.TogglePasswordVisibilityCommand.Execute(null);

        Assert.True(vm.IsPasswordVisible);
        Assert.Equal('\0', vm.PasswordMaskChar);
        Assert.Equal("Hide", vm.PasswordToggleText);
    }

    [Fact]
    public async Task WifiModal_ValidatesPasswordLength_ForSecuredNetworks()
    {
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        var securedWifi = new WifiNetworkInfo { Ssid = "Secured", Security = "WPA2" };
        await vm.OpenConnectModalAsync(securedWifi);

        // Blank password
        vm.WifiPasswordInput = "";
        await vm.ConnectWifiAsync();
        Assert.NotNull(vm.PasswordValidationError);
        Assert.Contains("Password is required", vm.PasswordValidationError);

        // Too short (< 8 chars)
        vm.WifiPasswordInput = "abc";
        Assert.NotNull(vm.PasswordValidationError);
        Assert.Contains("at least 8 characters", vm.PasswordValidationError);

        // Valid (>= 8 chars)
        vm.WifiPasswordInput = "12345678";
        Assert.Null(vm.PasswordValidationError);
    }

    [Fact]
    public async Task WifiModal_EnforcesSingleAdapterWarning_RequiresConfirmation()
    {
        var wifiManager = new FakeWifiNetworkManager();
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            wifiManager,
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "TargetNet", Security = "None" };
        await vm.OpenConnectModalAsync(targetWifi);

        // Single adapter topology detected
        Assert.True(vm.RequiresSingleAdapterWarning);

        // Attempting to connect without checkbox confirmation fails
        vm.UserConfirmedSingleAdapterSwitch = false;
        await vm.ConnectWifiAsync();

        Assert.NotNull(vm.ConnectErrorMessage);
        Assert.Contains("acknowledge", vm.ConnectErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Confirming allows connection
        vm.UserConfirmedSingleAdapterSwitch = true;
        await vm.ConnectWifiAsync();

        Assert.True(vm.ConnectSuccess);
        Assert.Null(vm.ConnectErrorMessage);
    }

    [Fact]
    public async Task WifiModal_OnConnectSuccess_SetsCctvInterfaceAndClearsPassword()
    {
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var wifiManager = new FakeWifiNetworkManager
        {
            ConnectResult = new WifiConnectionResult
            {
                Succeeded = true,
                ConnectedInterface = "en1",
                AssignedIp = "192.168.99.10",
                PreservedPrimaryConnection = true,
                Message = "Connected"
            }
        };

        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            wifiManager,
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "CCTV_WLAN", Security = "WPA2" };
        await vm.OpenConnectModalAsync(targetWifi);

        vm.TargetInterfaceForConnect = new NetworkInterfaceInfo { Name = "en1", HardwarePort = "Wi-Fi Adapter" };
        vm.RequiresSingleAdapterWarning = false;
        vm.WifiPasswordInput = "StrongPassphrase!";

        await vm.ConnectWifiAsync();

        Assert.True(vm.ConnectSuccess);
        // Zero password memory guarantee: password erased immediately
        Assert.Empty(vm.WifiPasswordInput);

        // CCTV route designated
        Assert.NotNull(selectionService.CctvInterface);
        Assert.Equal("en1", selectionService.CctvInterface.Name);
        Assert.NotNull(selectionService.CurrentCctvWifiConnection);
        Assert.Equal("CCTV_WLAN", selectionService.CurrentCctvWifiConnection.Ssid);
    }

    [Fact]
    public async Task WifiModal_OnClose_ClearsPasswordAndErrors()
    {
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "Net", Security = "WPA2" };
        await vm.OpenConnectModalAsync(targetWifi);

        vm.WifiPasswordInput = "sensitive_pass";
        vm.PasswordValidationError = "Some error";
        vm.ConnectErrorMessage = "Failed";

        vm.CloseConnectModal();

        Assert.False(vm.IsConnectModalOpen);
        Assert.Empty(vm.WifiPasswordInput);
        Assert.Null(vm.PasswordValidationError);
        Assert.Null(vm.ConnectErrorMessage);
        Assert.Equal(WifiConnectionState.AwaitingCredentials, vm.ConnectionState);
    }

    [Fact]
    public async Task WifiModal_AuthFailed_AllowsPasswordEditAndSuccessfulRetry()
    {
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var wifiManager = new FakeWifiNetworkManager
        {
            ConnectResult = new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.AuthenticationFailed,
                Message = "Authentication failed. Check the password and try again.",
                ErrorClassification = "OS CoreWLAN Authentication Failure (-3905)",
                CanRetry = true,
                ConnectedInterface = "en0"
            }
        };

        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            wifiManager,
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "CCTV_WLAN", Security = "WPA2" };
        await vm.OpenConnectModalAsync(targetWifi);

        vm.RequiresSingleAdapterWarning = false;
        vm.WifiPasswordInput = "WrongPass123";

        // First attempt: Fails with AuthenticationFailed
        await vm.ConnectWifiAsync();

        Assert.False(vm.ConnectSuccess);
        Assert.Equal(WifiConnectionState.AuthenticationFailed, vm.ConnectionState);
        Assert.True(vm.IsAuthFailed);
        Assert.True(vm.CanRetry);
        Assert.Contains("Authentication failed", vm.ConnectErrorMessage);
        // Password retained in field so operator can edit typo
        Assert.Equal("WrongPass123", vm.WifiPasswordInput);

        // Operator corrects password and retries
        vm.WifiPasswordInput = "CorrectPass123!";
        wifiManager.ConnectResult = new WifiConnectionResult
        {
            Succeeded = true,
            State = WifiConnectionState.Connected,
            ConnectedInterface = "en0",
            AssignedIp = "192.168.1.50",
            PreservedPrimaryConnection = true,
            Message = "Connected"
        };

        await vm.RetryConnectWifi();

        Assert.True(vm.ConnectSuccess);
        Assert.Equal(WifiConnectionState.Connected, vm.ConnectionState);
        Assert.False(vm.IsAuthFailed);
        // Password memory wiped immediately upon success
        Assert.Empty(vm.WifiPasswordInput);
    }

    [Fact]
    public async Task WifiModal_CancelConnect_TransitionsStateToCancelled()
    {
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        var targetWifi = new WifiNetworkInfo { Ssid = "CCTV_WLAN", Security = "WPA2" };
        await vm.OpenConnectModalAsync(targetWifi);

        // Simulate in-flight connecting state
        vm.IsConnectingWifi = true;
        vm.ConnectionState = WifiConnectionState.Connecting;

        // Operator invokes cancel
        vm.CancelConnectWifiCommand.Execute(null);

        Assert.False(vm.IsConnectingWifi);
        Assert.Equal(WifiConnectionState.Cancelled, vm.ConnectionState);
        Assert.True(vm.CanRetry);
        Assert.Contains("cancelled", vm.ConnectErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WifiModal_DetectsEnterprise8021X_PromptsSettingsRedirect()
    {
        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance),
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        var enterpriseWifi = new WifiNetworkInfo
        {
            Ssid = "Campus_Enterprise",
            Security = "WPA2 Enterprise (802.1X)"
        };

        await vm.OpenConnectModalAsync(enterpriseWifi);

        Assert.True(vm.IsUnsupportedAuthMode);
        Assert.Equal(WifiConnectionState.AuthenticationFailed, vm.ConnectionState);
        Assert.NotNull(vm.SuggestedAlternative);
        Assert.Contains("System Settings", vm.SuggestedAlternative);
    }

    [Fact]
    public async Task WifiModal_AuthorizedAlternatives_SwitchesToWiredEthernet()
    {
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var inventoryService = new FakeNetworkInventoryService();
        var wiredIface = inventoryService.Report.Interfaces.First(i => i.Name == "en3");

        var vm = new NetworkExplorerViewModel(
            inventoryService,
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            new FakeWifiNetworkManager(),
            NullLogger<NetworkExplorerViewModel>.Instance);

        var wifi = new WifiNetworkInfo { Ssid = "Locked_AP", Security = "WPA3" };
        await vm.OpenConnectModalAsync(wifi);

        // Verify wired interface is available in alternatives
        Assert.Contains(vm.AvailableWiredInterfaces, i => i.Name == "en3");

        // Toggle alternatives accordion
        Assert.False(vm.ShowAuthorizedAlternatives);
        vm.ToggleAuthorizedAlternativesCommand.Execute(null);
        Assert.True(vm.ShowAuthorizedAlternatives);

        // Select wired alternative directly without needing Wi-Fi password
        vm.SelectWiredAlternativeCommand.Execute(wiredIface);

        Assert.False(vm.IsConnectModalOpen);
        Assert.NotNull(selectionService.CctvInterface);
        Assert.Equal("en3", selectionService.CctvInterface.Name);
    }

    [Fact]
    public async Task WifiModal_KeychainOptIn_SavesAndLoadsCredentials()
    {
        var credStore = new FakeCredentialStore();
        var selectionService = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var wifiManager = new FakeWifiNetworkManager
        {
            ConnectResult = new WifiConnectionResult
            {
                Succeeded = true,
                State = WifiConnectionState.Connected,
                ConnectedInterface = "en0",
                AssignedIp = "192.168.1.10",
                PreservedPrimaryConnection = true,
                Message = "Connected"
            }
        };

        var vm = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            wifiManager,
            NullLogger<NetworkExplorerViewModel>.Instance,
            credStore);

        var targetWifi = new WifiNetworkInfo { Ssid = "Saved_AP", Security = "WPA2" };
        await vm.OpenConnectModalAsync(targetWifi);

        vm.RequiresSingleAdapterWarning = false;
        vm.WifiPasswordInput = "SuperSecretPSK99";
        vm.RememberCredentialsInStore = true; // Opt in

        await vm.ConnectWifiAsync();

        Assert.True(vm.ConnectSuccess);
        // Verify saved in store
        var saved = await credStore.GetCredentialsAsync("wifi:Saved_AP");
        Assert.NotNull(saved);
        Assert.Equal("SuperSecretPSK99", saved.Value.Password);

        // Next time opening modal for Saved_AP: password should auto-populate and checkbox be checked
        var vm2 = new NetworkExplorerViewModel(
            new FakeNetworkInventoryService(),
            selectionService,
            new FakeSubnetScanner(),
            new FakeDeviceRepository(),
            new FakeAuditService(),
            wifiManager,
            NullLogger<NetworkExplorerViewModel>.Instance,
            credStore);

        await vm2.OpenConnectModalAsync(targetWifi);
        Assert.Equal("SuperSecretPSK99", vm2.WifiPasswordInput);
        Assert.True(vm2.RememberCredentialsInStore);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, (string Username, string Password)> Credentials { get; } = new();

        public Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default)
        {
            Credentials[endpointId] = (username, password);
            return Task.CompletedTask;
        }

        public Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            if (Credentials.TryGetValue(endpointId, out var val))
            {
                return Task.FromResult<(string? Username, string? Password)?>(val);
            }
            return Task.FromResult<(string? Username, string? Password)?>(null);
        }

        public Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            Credentials.Remove(endpointId);
            return Task.CompletedTask;
        }
    }
}
