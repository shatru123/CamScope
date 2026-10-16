using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CamScope.App.ViewModels;

public sealed partial class NetworkExplorerViewModel : ViewModelBase
{
    private readonly INetworkInventoryService _inventoryService;
    private readonly INetworkSelectionService _selectionService;
    private readonly ISubnetScanner _subnetScanner;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IAuditService _auditService;
    private readonly ILogger<NetworkExplorerViewModel> _logger;

    private CancellationTokenSource? _subnetScanCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _primaryInternetText = "Detecting primary internet interface...";
    [ObservableProperty] private string _defaultGatewayText = "Detecting...";
    [ObservableProperty] private string _dnsServersText = "Detecting...";
    [ObservableProperty] private string _currentWifiText = "Disconnected / Not Detected";
    [ObservableProperty] private NetworkInterfaceInfo? _primaryInterface;
    [ObservableProperty] private NetworkInterfaceInfo? _selectedScanningInterface;
    [ObservableProperty] private WifiNetworkInfo? _selectedWifiNetwork;

    [ObservableProperty] private bool _isWifiPermitted = true;
    [ObservableProperty] private string? _permissionExplanation;

    [ObservableProperty] private string _subnetInput = "192.168.1.0/24";
    [ObservableProperty] private bool _isScanningSubnet;
    [ObservableProperty] private int _subnetProgressPercentage;
    [ObservableProperty] private string _subnetStatusMessage = "Ready to sweep subnet.";
    [ObservableProperty] private int _subnetDiscoveredCount;
    [ObservableProperty] private string? _subnetErrorMessage;

    [ObservableProperty] private ReachabilityDiagnostic? _reachability;

    [ObservableProperty] private bool _isWifiModalOpen;
    [ObservableProperty] private string _modalTargetSsid = string.Empty;

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];
    public ObservableCollection<WifiNetworkInfo> WifiNetworks { get; } = [];
    public ObservableCollection<CameraDevice> SubnetDiscoveredDevices { get; } = [];

    public NetworkExplorerViewModel(
        INetworkInventoryService inventoryService,
        INetworkSelectionService selectionService,
        ISubnetScanner subnetScanner,
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        ILogger<NetworkExplorerViewModel> logger)
    {
        _inventoryService = inventoryService;
        _selectionService = selectionService;
        _subnetScanner = subnetScanner;
        _deviceRepository = deviceRepository;
        _auditService = auditService;
        _logger = logger;

        _ = RefreshInventoryAsync();
    }

    [RelayCommand]
    public async Task RefreshInventoryAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var report = await _inventoryService.GetNetworkInventoryAsync(forceRefresh: true);

            Interfaces.Clear();
            foreach (var iface in report.Interfaces)
            {
                Interfaces.Add(iface);
            }

            WifiNetworks.Clear();
            foreach (var wifi in report.WifiNetworks)
            {
                WifiNetworks.Add(wifi);
            }

            PrimaryInterface = report.PrimaryInterface;
            PrimaryInternetText = report.PrimaryInterface != null
                ? $"{report.PrimaryInterface.Name} ({report.PrimaryInterface.HardwarePort}) - {report.PrimaryInterface.IPv4Address}"
                : "No active default internet route detected";

            DefaultGatewayText = !string.IsNullOrEmpty(report.SystemDefaultGateway)
                ? report.SystemDefaultGateway
                : "None (Direct / Local subnet)";

            DnsServersText = report.SystemDnsServers.Count > 0
                ? string.Join(", ", report.SystemDnsServers)
                : "None";

            CurrentWifiText = !string.IsNullOrEmpty(report.CurrentWifiSsid)
                ? report.CurrentWifiSsid
                : "Disconnected / Not connected to Wi-Fi";

            IsWifiPermitted = report.IsWifiPermitted;
            PermissionExplanation = report.PermissionMessage;

            // Maintain or initialize selected interface
            var currentSelected = _selectionService.CurrentScanningInterface;
            if (currentSelected != null && Interfaces.Any(i => i.Id == currentSelected.Id))
            {
                SelectedScanningInterface = Interfaces.First(i => i.Id == currentSelected.Id);
            }
            else
            {
                SelectedScanningInterface = report.PrimaryInterface ?? Interfaces.FirstOrDefault(i => i.IsUp && !i.IsLoopback);
                if (SelectedScanningInterface != null)
                {
                    _selectionService.SelectScanningInterface(SelectedScanningInterface);
                }
            }

            UpdateSubnetInputFromInterface(SelectedScanningInterface);
            UpdateReachabilityDiagnostic();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh network inventory");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedScanningInterfaceChanged(NetworkInterfaceInfo? value)
    {
        if (value != null)
        {
            _selectionService.SelectScanningInterface(value);
            UpdateSubnetInputFromInterface(value);
            _ = _auditService.LogAsync(
                AuditAction.NetworkInterfaceSelected,
                value.IPv4Address,
                succeeded: true,
                $"Operator selected scanning interface '{value.Name}' ({value.HardwarePort}) [{value.IPv4Address}]");
        }

        UpdateReachabilityDiagnostic();
    }

    private void UpdateSubnetInputFromInterface(NetworkInterfaceInfo? iface)
    {
        if (iface == null || string.IsNullOrWhiteSpace(iface.IPv4Address) || iface.IsLoopback)
            return;

        if (IPAddress.TryParse(iface.IPv4Address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            SubnetInput = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        }
    }

    private void UpdateReachabilityDiagnostic()
    {
        Reachability = EvaluateReachability(SelectedScanningInterface, DefaultGatewayText);
    }

    [RelayCommand]
    public void SelectInterface(NetworkInterfaceInfo? iface)
    {
        if (iface != null)
        {
            SelectedScanningInterface = iface;
        }
    }

    [RelayCommand]
    public async Task StartSubnetScanAsync()
    {
        if (SelectedScanningInterface == null)
        {
            SubnetErrorMessage = "Please select a network interface before scanning.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SubnetInput))
        {
            SubnetErrorMessage = "Please enter a valid CIDR subnet prefix (e.g. 192.168.1.0/24).";
            return;
        }

        IsScanningSubnet = true;
        SubnetErrorMessage = null;
        SubnetDiscoveredCount = 0;
        SubnetProgressPercentage = 0;
        SubnetStatusMessage = $"Starting directed scan on {SubnetInput}...";
        SubnetDiscoveredDevices.Clear();

        _subnetScanCts = new CancellationTokenSource();

        await _auditService.LogAsync(
            AuditAction.SubnetScanStarted,
            SubnetInput,
            succeeded: true,
            $"Started directed subnet sweep on interface '{SelectedScanningInterface.Name}' for CIDR '{SubnetInput}'");

        var progress = new Progress<DiscoveryProgressReport>(report =>
        {
            SubnetProgressPercentage = report.Percentage;
            SubnetStatusMessage = report.CurrentStep;
            SubnetDiscoveredCount = report.DevicesFound;

            if (report.LatestDevice != null && !SubnetDiscoveredDevices.Any(d => d.EndpointId == report.LatestDevice.EndpointId))
            {
                SubnetDiscoveredDevices.Add(report.LatestDevice);
            }
        });

        try
        {
            var request = new SubnetScanRequest
            {
                TargetInterface = SelectedScanningInterface,
                CidrOrSubnet = SubnetInput.Trim(),
                TimeoutPerHost = TimeSpan.FromMilliseconds(500),
                MaxConcurrency = 16
            };

            var discovered = await _subnetScanner.ScanSubnetAsync(request, progress, _subnetScanCts.Token);

            foreach (var dev in discovered)
            {
                await _deviceRepository.AddOrUpdateAsync(dev);
                if (!SubnetDiscoveredDevices.Any(d => d.EndpointId == dev.EndpointId))
                {
                    SubnetDiscoveredDevices.Add(dev);
                }
            }

            SubnetDiscoveredCount = discovered.Count;
            SubnetStatusMessage = $"Subnet scan complete. Found {discovered.Count} camera device(s).";

            await _auditService.LogAsync(
                AuditAction.SubnetScanCompleted,
                SubnetInput,
                succeeded: true,
                $"Completed directed subnet scan on interface '{SelectedScanningInterface.Name}'. Found {discovered.Count} device(s).");
        }
        catch (OperationCanceledException)
        {
            SubnetStatusMessage = "Subnet scan cancelled by operator.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subnet scan encountered an error");
            SubnetErrorMessage = $"Scan failed: {ex.Message}";
            SubnetStatusMessage = "Scan failed.";
        }
        finally
        {
            IsScanningSubnet = false;
            _subnetScanCts?.Dispose();
            _subnetScanCts = null;
        }
    }

    [RelayCommand]
    public void CancelSubnetScan()
    {
        _subnetScanCts?.Cancel();
    }

    [RelayCommand]
    public void OpenWifiModal(WifiNetworkInfo? wifi)
    {
        if (wifi == null) return;
        ModalTargetSsid = wifi.Ssid;
        IsWifiModalOpen = true;
    }

    [RelayCommand]
    public void CloseWifiModal()
    {
        IsWifiModalOpen = false;
    }

    [RelayCommand]
    public void OpenSystemSettings()
    {
        CloseWifiModal();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = "/System/Applications/System\\ Settings.app",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open macOS System Settings directly");
        }
    }

    public static ReachabilityDiagnostic EvaluateReachability(NetworkInterfaceInfo? iface, string? defaultGateway)
    {
        if (iface == null)
        {
            return new ReachabilityDiagnostic
            {
                InterfaceName = "None",
                Severity = DiagnosticSeverity.Warning,
                SummaryExplanation = "No network adapter selected for camera discovery.",
                SuggestedActions = ["Select an active network adapter from the table above."]
            };
        }

        var actions = new List<string>();
        var severity = DiagnosticSeverity.Info;
        var summaryParts = new List<string>();

        bool isMulticast = iface.SupportsMulticast;
        bool hasGateway = !string.IsNullOrEmpty(iface.GatewayAddress);
        bool isRoutable = !iface.IsLoopback && !string.IsNullOrEmpty(iface.IPv4Address) && iface.IsUp;

        if (!iface.IsUp)
        {
            severity = DiagnosticSeverity.Error;
            summaryParts.Add($"Interface {iface.Name} is currently down (link disconnected).");
            actions.Add("Connect an Ethernet cable or enable Wi-Fi on this adapter.");
        }
        else if (iface.IsLoopback)
        {
            severity = DiagnosticSeverity.Warning;
            summaryParts.Add("Loopback adapter (127.0.0.1) selected. Only local simulated/mock cameras can be reached.");
            actions.Add("Select a physical Wi-Fi or Ethernet adapter to discover physical network cameras.");
        }
        else
        {
            if (iface.IsPrimaryInternet)
            {
                summaryParts.Add($"Interface {iface.Name} is your primary internet connection (Gateway: {iface.GatewayAddress ?? defaultGateway}). CamScope binds probes safely in app-scoped mode without altering system internet routing.");
            }
            else
            {
                summaryParts.Add($"Interface {iface.Name} is a dedicated secondary adapter ({iface.HardwarePort}). CamScope binds discovery strictly to this port, leaving your primary internet connection isolated and untouched.");
            }

            if (!isMulticast)
            {
                severity = DiagnosticSeverity.Warning;
                summaryParts.Add("Multicast (WS-Discovery 239.255.255.250) is reported as unsupported by this interface.");
                actions.Add("Use CamScope's Directed Subnet Scanner to sweep the IP subnet via directed UDP unicast probes.");
            }
            else
            {
                summaryParts.Add("Multicast traffic is supported. WS-Discovery standard probes (port 3702) are ready.");
            }

            if (!hasGateway)
            {
                summaryParts.Add("No default gateway configured on this interface (isolated camera LAN / direct link).");
                string subnetPrefix = iface.IPv4Address.Contains('.')
                    ? iface.IPv4Address[..iface.IPv4Address.LastIndexOf('.')] + ".0/24"
                    : "same subnet";
                actions.Add($"Ensure connected cameras have static IP addresses in {subnetPrefix}.");
            }
        }

        return new ReachabilityDiagnostic
        {
            InterfaceName = iface.Name,
            IsMulticastSupported = isMulticast,
            IsSubnetRoutable = isRoutable,
            HasDefaultGateway = hasGateway,
            Severity = severity,
            SummaryExplanation = string.Join(" ", summaryParts),
            SuggestedActions = actions
        };
    }
}
