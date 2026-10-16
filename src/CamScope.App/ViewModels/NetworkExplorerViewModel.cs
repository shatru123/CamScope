using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Discovery.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CamScope.App.ViewModels;

public sealed partial class NetworkExplorerViewModel : ViewModelBase, IDisposable
{
    private readonly INetworkInventoryService _inventoryService;
    private readonly INetworkSelectionService _selectionService;
    private readonly ISubnetScanner _subnetScanner;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IAuditService _auditService;
    private readonly IWifiNetworkManager _wifiNetworkManager;
    private readonly ILogger<NetworkExplorerViewModel> _logger;

    private CancellationTokenSource? _subnetScanCts;
    private bool _disposed;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _primaryInternetText = "Detecting primary internet interface...";
    [ObservableProperty] private string _defaultGatewayText = "Detecting...";
    [ObservableProperty] private string _dnsServersText = "Detecting...";
    [ObservableProperty] private string _currentWifiText = "Disconnected / Not Detected";
    [ObservableProperty] private NetworkInterfaceInfo? _primaryInterface;
    [ObservableProperty] private NetworkInterfaceInfo? _selectedScanningInterface;
    [ObservableProperty] private WifiNetworkInfo? _selectedWifiNetwork;

    [ObservableProperty] private InterfaceBindingStatus? _bindingStatus;
    [ObservableProperty] private string _bindingStatusText = "Interface binding uninitialized";
    [ObservableProperty] private bool _isStrictBindingActive;

    [ObservableProperty] private string _routingStabilityText = "Routing table invariant: Protected (Default route, DNS, and primary interface unchanged)";
    [ObservableProperty] private bool _isRoutingStable = true;
    [ObservableProperty] private bool _hasMultiplePhysicalAdapters;
    [ObservableProperty] private string _adapterTopologyExplanation = string.Empty;

    // CCTV Route Properties
    [ObservableProperty] private NetworkInterfaceInfo? _cctvInterface;
    [ObservableProperty] private WifiConnectionInfo? _currentCctvWifiConnection;
    [ObservableProperty] private string _cctvStatusText = "No dedicated secondary CCTV route configured.";
    [ObservableProperty] private bool _hasCctvConnection;

    [ObservableProperty] private bool _isWifiPermitted = true;
    [ObservableProperty] private string? _permissionExplanation;

    // Subnet Scanner Properties
    [ObservableProperty] private string _subnetInput = "192.168.1.0/24";
    [ObservableProperty] private bool _isScanningSubnet;
    [ObservableProperty] private int _subnetProgressPercentage;
    [ObservableProperty] private string _subnetStatusMessage = "Ready to sweep subnet.";
    [ObservableProperty] private int _subnetDiscoveredCount;
    [ObservableProperty] private string? _subnetErrorMessage;

    [ObservableProperty] private ReachabilityDiagnostic? _reachability;

    // In-App Wi-Fi Browser Search, Filter, Sort
    [ObservableProperty] private string _wifiSearchQuery = string.Empty;
    [ObservableProperty] private string _selectedSecurityFilter = "All";
    [ObservableProperty] private string _selectedBandFilter = "All";
    [ObservableProperty] private string _selectedSortOption = "Signal Strength";

    public IReadOnlyList<string> SecurityFilterOptions { get; } = ["All", "Secured", "Open"];
    public IReadOnlyList<string> BandFilterOptions { get; } = ["All", "5 GHz", "2.4 GHz"];
    public IReadOnlyList<string> SortOptions { get; } = ["Signal Strength", "SSID (A-Z)", "Channel"];

    // In-App Wi-Fi Connect Modal Properties
    [ObservableProperty] private bool _isConnectModalOpen;
    [ObservableProperty] private bool _isWifiModalOpen; // Backward compatibility with previous binding
    [ObservableProperty] private string _modalTargetSsid = string.Empty;
    [ObservableProperty] private WifiNetworkInfo? _targetWifi;
    [ObservableProperty] private string _wifiPasswordInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordMaskChar))]
    [NotifyPropertyChangedFor(nameof(PasswordToggleText))]
    private bool _isPasswordVisible;

    public char PasswordMaskChar => IsPasswordVisible ? '\0' : '•';
    public string PasswordToggleText => IsPasswordVisible ? "Hide" : "Show";

    [ObservableProperty] private string? _passwordValidationError;
    [ObservableProperty] private bool _isConnectingWifi;
    [ObservableProperty] private string? _connectStatusMessage;
    [ObservableProperty] private string? _connectErrorMessage;
    [ObservableProperty] private bool _connectSuccess;
    [ObservableProperty] private string? _connectSuccessMessage;
    [ObservableProperty] private NetworkInterfaceInfo? _targetInterfaceForConnect;
    [ObservableProperty] private bool _requiresSingleAdapterWarning;
    [ObservableProperty] private bool _userConfirmedSingleAdapterSwitch;
    [ObservableProperty] private bool _canSupportConcurrentConnections;

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];
    public ObservableCollection<WifiNetworkInfo> WifiNetworks { get; } = [];
    public ObservableCollection<WifiNetworkInfo> FilteredWifiNetworks { get; } = [];
    public ObservableCollection<NetworkInterfaceInfo> AvailableWifiInterfaces { get; } = [];
    public ObservableCollection<CameraDevice> SubnetDiscoveredDevices { get; } = [];

    public NetworkExplorerViewModel(
        INetworkInventoryService inventoryService,
        INetworkSelectionService selectionService,
        ISubnetScanner subnetScanner,
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        IWifiNetworkManager wifiNetworkManager,
        ILogger<NetworkExplorerViewModel> logger)
    {
        _inventoryService = inventoryService;
        _selectionService = selectionService;
        _subnetScanner = subnetScanner;
        _deviceRepository = deviceRepository;
        _auditService = auditService;
        _wifiNetworkManager = wifiNetworkManager;
        _logger = logger;

        _selectionService.CctvInterfaceChanged += HandleCctvInterfaceChanged;
        UpdateCctvStatus();

        _ = RefreshInventoryAsync();
    }

    public NetworkExplorerViewModel(
        INetworkInventoryService inventoryService,
        INetworkSelectionService selectionService,
        ISubnetScanner subnetScanner,
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        ILogger<NetworkExplorerViewModel> logger)
        : this(inventoryService, selectionService, subnetScanner, deviceRepository, auditService, new CamScope.Infrastructure.Network.UnsupportedPlatformWifiNetworkManager(), logger)
    {
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
            ApplyWifiFilterAndSort();

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

            // Evaluate multi-adapter physical topology
            var activePhysicalAdapters = Interfaces.Where(i => !i.IsLoopback && i.IsUp).ToList();
            HasMultiplePhysicalAdapters = activePhysicalAdapters.Count > 1;
            CanSupportConcurrentConnections = _wifiNetworkManager.CanSupportConcurrentConnections;

            if (HasMultiplePhysicalAdapters)
            {
                AdapterTopologyExplanation = $"Multiple active network adapters detected ({string.Join(", ", activePhysicalAdapters.Select(a => $"{a.Name} [{a.HardwarePort}]"))}). CamScope can bind camera discovery and streaming to a secondary adapter without altering your primary internet connection.";
            }
            else
            {
                AdapterTopologyExplanation = "Single active network adapter detected. Note: A single physical Wi-Fi radio cannot be joined to two SSIDs simultaneously. For an isolated camera network, connect a secondary physical adapter (such as a USB-C Ethernet or Wi-Fi adapter) to discover cameras without affecting your internet connection.";
            }

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

            UpdateBindingStatus(SelectedScanningInterface);
            UpdateSubnetInputFromInterface(SelectedScanningInterface);
            UpdateReachabilityDiagnostic();
            UpdateCctvStatus();
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
        UpdateBindingStatus(value);

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

    private void UpdateBindingStatus(NetworkInterfaceInfo? iface)
    {
        BindingStatus = SocketInterfaceBinder.InspectBindingSupport(iface);
        IsStrictBindingActive = BindingStatus.IsStrictlyBound;
        BindingStatusText = BindingStatus.Description;
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

        if (string.IsNullOrWhiteSpace(SelectedScanningInterface.IPv4Address))
        {
            SubnetErrorMessage = $"Selected adapter '{SelectedScanningInterface.Name}' ({SelectedScanningInterface.HardwarePort}) does not have an active IPv4 address. Connect a network cable or assign an IP address before scanning.";
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

        NetworkRoutingSnapshot? beforeRouting = null;
        try
        {
            beforeRouting = await _inventoryService.CaptureRoutingSnapshotAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not capture pre-scan routing snapshot");
        }

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

            try
            {
                var afterRouting = await _inventoryService.CaptureRoutingSnapshotAsync();
                var invariant = _inventoryService.VerifyRoutingInvariant(beforeRouting, afterRouting);
                IsRoutingStable = invariant.IsStable;
                RoutingStabilityText = invariant.StatusText;
                if (!invariant.IsStable && invariant.MutationDetails.Count > 0)
                {
                    SubnetErrorMessage = $"Routing warning: {string.Join("; ", invariant.MutationDetails)}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify post-scan routing invariant");
            }
        }
    }

    [RelayCommand]
    public void CancelSubnetScan()
    {
        _subnetScanCts?.Cancel();
    }

    private void HandleCctvInterfaceChanged(NetworkInterfaceInfo? iface)
    {
        UpdateCctvStatus();
    }

    private void UpdateCctvStatus()
    {
        CctvInterface = _selectionService.CctvInterface;
        CurrentCctvWifiConnection = _selectionService.CurrentCctvWifiConnection;
        HasCctvConnection = CctvInterface != null;

        if (CctvInterface != null)
        {
            var wifiPart = CurrentCctvWifiConnection != null
                ? $" (Wi-Fi SSID: \"{CurrentCctvWifiConnection.Ssid}\")"
                : "";
            CctvStatusText = $"Active Dedicated CCTV Route: {CctvInterface.Name} [{CctvInterface.HardwarePort}] - {CctvInterface.IPv4Address}{wifiPart}";
        }
        else
        {
            CctvStatusText = "No dedicated secondary CCTV route configured. CamScope is using primary adapter with app-scoped binding.";
        }
    }

    [RelayCommand]
    public void SetAsCctvInterface(NetworkInterfaceInfo? iface)
    {
        if (iface != null)
        {
            _selectionService.SetCctvInterface(iface);
            _ = _auditService.LogAsync(
                AuditAction.NetworkInterfaceSelected,
                iface.IPv4Address,
                succeeded: true,
                $"Operator designated '{iface.Name}' ({iface.HardwarePort}) as dedicated CCTV interface");
        }
    }

    [RelayCommand]
    public void ClearCctvInterface()
    {
        _selectionService.SetCctvInterface(null, null);
    }

    // --- In-App Wi-Fi Filtering and Sorting ---

    partial void OnWifiSearchQueryChanged(string value) => ApplyWifiFilterAndSort();
    partial void OnSelectedSecurityFilterChanged(string value) => ApplyWifiFilterAndSort();
    partial void OnSelectedBandFilterChanged(string value) => ApplyWifiFilterAndSort();
    partial void OnSelectedSortOptionChanged(string value) => ApplyWifiFilterAndSort();

    [RelayCommand]
    public void SetSecurityFilter(string filter)
    {
        SelectedSecurityFilter = filter;
    }

    [RelayCommand]
    public void SetBandFilter(string band)
    {
        SelectedBandFilter = band;
    }

    [RelayCommand]
    public void SetSortOption(string option)
    {
        SelectedSortOption = option;
    }

    public void ApplyWifiFilterAndSort()
    {
        var query = WifiNetworks.AsEnumerable();

        // 1. Search Query
        if (!string.IsNullOrWhiteSpace(WifiSearchQuery))
        {
            var q = WifiSearchQuery.Trim();
            query = query.Where(w =>
                (w.Ssid != null && w.Ssid.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (w.Bssid != null && w.Bssid.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        // 2. Security Filter
        if (string.Equals(SelectedSecurityFilter, "Secured", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(w => w.IsSecured);
        }
        else if (string.Equals(SelectedSecurityFilter, "Open", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(w => !w.IsSecured);
        }

        // 3. Band Filter
        if (string.Equals(SelectedBandFilter, "5 GHz", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(w => w.BandGroup == "5 GHz");
        }
        else if (string.Equals(SelectedBandFilter, "2.4 GHz", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(w => w.BandGroup == "2.4 GHz");
        }

        // 4. Sort
        query = SelectedSortOption switch
        {
            "SSID (A-Z)" => query.OrderBy(w => w.Ssid, StringComparer.OrdinalIgnoreCase),
            "Channel" => query.OrderBy(w => w.Channel).ThenBy(w => w.Ssid, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(w => w.IsCurrent)
                      .ThenByDescending(w => w.Rssi.HasValue)
                      .ThenByDescending(w => w.Rssi ?? -100)
                      .ThenBy(w => w.Ssid, StringComparer.OrdinalIgnoreCase)
        };

        FilteredWifiNetworks.Clear();
        foreach (var item in query)
        {
            FilteredWifiNetworks.Add(item);
        }
    }

    // --- In-App Wi-Fi Connect Modal & Actions ---

    [RelayCommand]
    public async Task OpenConnectModalAsync(WifiNetworkInfo? wifi)
    {
        if (wifi == null) return;

        TargetWifi = wifi;
        ModalTargetSsid = wifi.Ssid;
        WifiPasswordInput = string.Empty;
        PasswordValidationError = null;
        ConnectErrorMessage = null;
        ConnectStatusMessage = null;
        ConnectSuccess = false;
        ConnectSuccessMessage = null;
        IsPasswordVisible = false;
        UserConfirmedSingleAdapterSwitch = false;

        AvailableWifiInterfaces.Clear();
        var wifiIfaces = await _wifiNetworkManager.GetWifiInterfacesAsync();
        if (wifiIfaces.Count == 0)
        {
            wifiIfaces = Interfaces.Where(i => i.InterfaceKind == NetworkInterfaceKind.WiFi ||
                                               (i.HardwarePort ?? "").Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                               (i.HardwarePort ?? "").Contains("AirPort", StringComparison.OrdinalIgnoreCase))
                                   .ToList();
        }

        foreach (var iface in wifiIfaces)
        {
            AvailableWifiInterfaces.Add(iface);
        }

        // Default to secondary Wi-Fi adapter if available, otherwise first
        TargetInterfaceForConnect = AvailableWifiInterfaces.FirstOrDefault(i => !i.IsPrimaryInternet)
                                    ?? AvailableWifiInterfaces.FirstOrDefault();

        CanSupportConcurrentConnections = _wifiNetworkManager.CanSupportConcurrentConnections;

        RequiresSingleAdapterWarning = TargetInterfaceForConnect == null ||
                                       TargetInterfaceForConnect.IsPrimaryInternet ||
                                       AvailableWifiInterfaces.Count <= 1;

        IsConnectModalOpen = true;
        IsWifiModalOpen = true;
    }

    public void OpenConnectModal(WifiNetworkInfo? wifi)
    {
        _ = OpenConnectModalAsync(wifi);
    }

    [RelayCommand]
    public void OpenWifiModal(WifiNetworkInfo? wifi)
    {
        _ = OpenConnectModalAsync(wifi);
    }

    [RelayCommand]
    public void CloseConnectModal()
    {
        WifiPasswordInput = string.Empty;
        PasswordValidationError = null;
        ConnectErrorMessage = null;
        ConnectStatusMessage = null;
        ConnectSuccess = false;
        ConnectSuccessMessage = null;
        IsConnectModalOpen = false;
        IsWifiModalOpen = false;
    }

    [RelayCommand]
    public void CloseWifiModal()
    {
        CloseConnectModal();
    }

    [RelayCommand]
    public void TogglePasswordVisibility()
    {
        IsPasswordVisible = !IsPasswordVisible;
    }

    partial void OnTargetInterfaceForConnectChanged(NetworkInterfaceInfo? value)
    {
        RequiresSingleAdapterWarning = value == null ||
                                       value.IsPrimaryInternet ||
                                       AvailableWifiInterfaces.Count <= 1;
        UserConfirmedSingleAdapterSwitch = false;
    }

    partial void OnWifiPasswordInputChanged(string value)
    {
        if (TargetWifi?.IsSecured == true)
        {
            if (!string.IsNullOrEmpty(value) && value.Length < 8)
            {
                PasswordValidationError = "WPA/WPA2/WPA3 password must be at least 8 characters.";
            }
            else
            {
                PasswordValidationError = null;
            }
        }
        else
        {
            PasswordValidationError = null;
        }
    }

    [RelayCommand]
    public async Task ConnectWifiAsync()
    {
        if (TargetWifi == null)
        {
            ConnectErrorMessage = "No Wi-Fi network selected.";
            return;
        }

        if (TargetInterfaceForConnect == null)
        {
            ConnectErrorMessage = "No network interface selected for connection.";
            return;
        }

        // Validate password
        if (TargetWifi.IsSecured)
        {
            if (string.IsNullOrWhiteSpace(WifiPasswordInput))
            {
                PasswordValidationError = "Password is required for secured Wi-Fi network.";
                return;
            }

            if (WifiPasswordInput.Length < 8)
            {
                PasswordValidationError = "WPA/WPA2/WPA3 password must be at least 8 characters.";
                return;
            }
        }

        // Validate concurrency confirmation
        if (RequiresSingleAdapterWarning && !UserConfirmedSingleAdapterSwitch)
        {
            ConnectErrorMessage = "Please acknowledge that connecting on a single adapter will alter your Mac's primary internet connection.";
            return;
        }

        IsConnectingWifi = true;
        ConnectStatusMessage = $"Connecting to \"{TargetWifi.Ssid}\" on {TargetInterfaceForConnect.Name}...";
        ConnectErrorMessage = null;
        ConnectSuccess = false;
        ConnectSuccessMessage = null;
        PasswordValidationError = null;

        try
        {
            var req = new WifiConnectRequest
            {
                Ssid = TargetWifi.Ssid,
                Password = WifiPasswordInput,
                InterfaceName = TargetInterfaceForConnect.Name,
                AllowSwitchingPrimaryIfSingleAdapter = UserConfirmedSingleAdapterSwitch
            };

            var result = await _wifiNetworkManager.ConnectAsync(req);

            if (result.Succeeded)
            {
                ConnectSuccess = true;
                if (result.PreservedPrimaryConnection)
                {
                    ConnectSuccessMessage = $"Connected to '{TargetWifi.Ssid}' on dedicated secondary adapter {result.ConnectedInterface} (IP: {result.AssignedIp ?? "DHCP Pending"}). Primary internet route preserved.";

                    var cctvIface = Interfaces.FirstOrDefault(i => i.Name == result.ConnectedInterface) ?? TargetInterfaceForConnect;
                    _selectionService.SetCctvInterface(cctvIface, new WifiConnectionInfo
                    {
                        Ssid = TargetWifi.Ssid,
                        InterfaceName = result.ConnectedInterface ?? TargetInterfaceForConnect.Name,
                        IPv4Address = result.AssignedIp,
                        IsPrimaryInternet = false,
                        IsCctvNetwork = true
                    });
                }
                else
                {
                    ConnectSuccessMessage = $"Connected to '{TargetWifi.Ssid}' on adapter {result.ConnectedInterface}. Primary connection switched as requested.";
                }

                // Clear password immediately (never retain in memory)
                WifiPasswordInput = string.Empty;

                _ = _auditService.LogAsync(
                    AuditAction.NetworkInterfaceSelected,
                    result.AssignedIp,
                    succeeded: true,
                    $"Wi-Fi connected to SSID '{TargetWifi.Ssid}' on interface '{result.ConnectedInterface}'. PreservedPrimary: {result.PreservedPrimaryConnection}");

                _ = RefreshInventoryAsync();
            }
            else
            {
                ConnectErrorMessage = $"Connection failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error connecting to Wi-Fi {Ssid}", TargetWifi.Ssid);
            ConnectErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsConnectingWifi = false;
        }
    }

    [RelayCommand]
    public async Task DisconnectWifiAsync()
    {
        if (TargetInterfaceForConnect == null)
        {
            ConnectErrorMessage = "No network adapter selected to disconnect.";
            return;
        }

        IsConnectingWifi = true;
        ConnectStatusMessage = $"Disconnecting {TargetInterfaceForConnect.Name}...";
        ConnectErrorMessage = null;
        ConnectSuccess = false;
        ConnectSuccessMessage = null;

        try
        {
            var success = await _wifiNetworkManager.DisconnectAsync(TargetInterfaceForConnect.Name);
            if (success)
            {
                ConnectSuccess = true;
                ConnectSuccessMessage = $"Successfully disconnected {TargetInterfaceForConnect.Name}.";
                if (_selectionService.CctvInterface?.Name == TargetInterfaceForConnect.Name)
                {
                    _selectionService.SetCctvInterface(null, null);
                }
                _ = RefreshInventoryAsync();
            }
            else
            {
                ConnectErrorMessage = $"Failed to disconnect {TargetInterfaceForConnect.Name}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting {Iface}", TargetInterfaceForConnect.Name);
            ConnectErrorMessage = $"Disconnect error: {ex.Message}";
        }
        finally
        {
            IsConnectingWifi = false;
        }
    }

    [RelayCommand]
    public void OpenSystemSettings()
    {
        CloseConnectModal();

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selectionService.CctvInterfaceChanged -= HandleCctvInterfaceChanged;
        _subnetScanCts?.Dispose();
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
