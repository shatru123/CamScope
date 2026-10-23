using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class DiscoveryViewModel : ViewModelBase
{
    private readonly INetworkDiscoveryEngine _realEngine;
    private readonly INetworkDiscoveryEngine _demoEngine;
    private readonly INetworkInterfaceProvider _interfaceProvider;
    private readonly IDeviceRepository _deviceRepository;
    private readonly IAuditService _auditService;
    private readonly Func<bool> _isDemoModeFunc;
    private readonly INetworkSelectionService? _selectionService;
    private readonly ISubnetScanner? _subnetScanner;
    private readonly DeviceManagementService? _deviceService;

    private CancellationTokenSource? _subnetCts;

    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;
    [ObservableProperty] private bool _includeLoopback;
    [ObservableProperty] private bool _isAuthorizedConfirmed;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private int _progressPercentage;
    [ObservableProperty] private string _progressStep = "Ready to discover.";
    [ObservableProperty] private int _devicesFoundCount;
    [ObservableProperty] private string? _errorMessage;

    // Discovery Mode: 0 = WS-Discovery (Multicast), 1 = Directed Subnet Scan (CIDR), 2 = Manual Add
    [ObservableProperty] private int _selectedDiscoveryMode = 0;

    // Subnet Scan settings
    [ObservableProperty] private string _subnetCidr = "192.168.1.0/24";
    [ObservableProperty] private int _subnetTimeoutMs = 400;
    [ObservableProperty] private int _subnetConcurrency = 16;
    [ObservableProperty] private int _subnetProbePort = 3702;

    // Manual Add settings
    [ObservableProperty] private int _manualProtocolIndex = 0; // 0: ONVIF, 1: Direct RTSP
    [ObservableProperty] private string _manualCameraName = "Manual Camera";
    [ObservableProperty] private string _manualHostOrUrl = "192.168.1.100";
    [ObservableProperty] private int _manualPort = 80;
    [ObservableProperty] private string _manualPath = "/onvif/device_service";
    [ObservableProperty] private string _manualUsername = "";
    [ObservableProperty] private string _manualPassword = "";
    [ObservableProperty] private string _manualLocation = "";
    [ObservableProperty] private string _manualRtspTransport = "tcp";
    [ObservableProperty] private string? _manualAddStatus;
    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string? _connectionTestResultText;

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];
    public ObservableCollection<CameraDevice> DiscoveredDevices { get; } = [];

    public bool CanStartDiscovery => SelectedInterface != null && IsAuthorizedConfirmed && !IsDiscovering;

    public string VlanDisclosureText =>
        "Network Topology Notice: WS-Discovery multicast (239.255.255.250:3702) does not traverse router/VLAN boundaries without an IGMP proxy, and is blocked on Wi-Fi networks with Client/AP Isolation enabled. If cameras reside on a dedicated CCTV VLAN or routed subnet, use Directed Subnet Scan (CIDR) or Manual Camera Add.";

    public DiscoveryViewModel(
        INetworkDiscoveryEngine realEngine,
        INetworkDiscoveryEngine demoEngine,
        INetworkInterfaceProvider interfaceProvider,
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        Func<bool> isDemoModeFunc,
        INetworkSelectionService? selectionService = null,
        ISubnetScanner? subnetScanner = null,
        DeviceManagementService? deviceService = null)
    {
        _realEngine = realEngine;
        _demoEngine = demoEngine;
        _interfaceProvider = interfaceProvider;
        _deviceRepository = deviceRepository;
        _auditService = auditService;
        _isDemoModeFunc = isDemoModeFunc;
        _selectionService = selectionService;
        _subnetScanner = subnetScanner;
        _deviceService = deviceService;

        if (_selectionService != null)
        {
            _selectionService.SelectionChanged += OnExternalSelectionChanged;
        }

        try
        {
            _interfaceProvider.InterfacesChanged += () =>
            {
                Dispatcher.UIThread.Post(LoadInterfaces);
            };
        }
        catch
        {
            // Ignore event subscription issues in mock/testing
        }

        LoadInterfaces();
    }

    private void OnExternalSelectionChanged(NetworkInterfaceInfo? iface)
    {
        if (iface == null) return;
        var matching = Interfaces.FirstOrDefault(i => i.Id == iface.Id || i.IPv4Address == iface.IPv4Address);
        if (matching != null && matching != SelectedInterface)
        {
            SelectedInterface = matching;
        }
    }

    public void LoadInterfaces()
    {
        Interfaces.Clear();
        var ifaces = _interfaceProvider.GetEligibleInterfaces(IncludeLoopback);
        foreach (var i in ifaces)
        {
            Interfaces.Add(i);
        }

        var currentSelected = _selectionService?.CurrentScanningInterface;
        if (currentSelected != null)
        {
            SelectedInterface = Interfaces.FirstOrDefault(i => i.Id == currentSelected.Id || i.IPv4Address == currentSelected.IPv4Address);
        }

        SelectedInterface ??= Interfaces.FirstOrDefault();
    }

    partial void OnIncludeLoopbackChanged(bool value) => LoadInterfaces();

    partial void OnSelectedInterfaceChanged(NetworkInterfaceInfo? value)
    {
        OnPropertyChanged(nameof(CanStartDiscovery));
        if (value != null)
        {
            _selectionService?.SelectScanningInterface(value);
            SubnetCidr = CalculateDefaultCidr(value.IPv4Address, value.SubnetMask);
        }
    }

    partial void OnIsAuthorizedConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanStartDiscovery));
    partial void OnIsDiscoveringChanged(bool value) => OnPropertyChanged(nameof(CanStartDiscovery));

    partial void OnManualProtocolIndexChanged(int value)
    {
        if (value == 0) // ONVIF
        {
            ManualPort = 80;
            ManualHostOrUrl = "192.168.1.100";
        }
        else // Direct RTSP
        {
            ManualPort = 554;
            ManualHostOrUrl = "rtsp://192.168.1.100:554/stream1";
        }
    }

    [RelayCommand]
    public async Task StartDiscoveryAsync()
    {
        if (SelectedInterface == null || !IsAuthorizedConfirmed)
            return;

        IsDiscovering = true;
        ProgressPercentage = 0;
        ProgressStep = "Preparing WS-Discovery probes...";
        DevicesFoundCount = 0;
        ErrorMessage = null;
        DiscoveredDevices.Clear();

        var isDemo = _isDemoModeFunc();
        var engine = isDemo ? _demoEngine : _realEngine;

        await _auditService.LogAsync(
            AuditAction.DiscoveryStarted,
            SelectedInterface.IPv4Address,
            succeeded: true,
            $"Started discovery session on interface '{SelectedInterface.Name}' ({SelectedInterface.IPv4Address}) [DemoMode: {isDemo}]");

        var progress = new Progress<DiscoveryProgressReport>(report =>
        {
            ProgressPercentage = report.Percentage;
            ProgressStep = report.CurrentStep;
            DevicesFoundCount = report.DevicesFound;

            if (report.LatestDevice != null)
            {
                AddOrUpdateDiscovered(report.LatestDevice);
            }
        });

        try
        {
            var results = await engine.DiscoverAsync(SelectedInterface, CancellationToken.None, progress);

            foreach (var dev in results)
            {
                AddOrUpdateDiscovered(dev);
            }

            await _auditService.LogAsync(
                AuditAction.DiscoveryCompleted,
                SelectedInterface.IPv4Address,
                succeeded: true,
                $"Discovery completed with {results.Count} devices discovered.");
        }
        catch (OperationCanceledException)
        {
            ProgressStep = "Discovery cancelled by user.";
            await _auditService.LogAsync(
                AuditAction.DiscoveryCancelled,
                SelectedInterface.IPv4Address,
                succeeded: true,
                "Discovery session cancelled by user.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Discovery error: {ex.Message}";
            ProgressStep = "Discovery failed.";
            await _auditService.LogAsync(
                AuditAction.DiscoveryCompleted,
                SelectedInterface.IPv4Address,
                succeeded: false,
                $"Discovery failed on interface '{SelectedInterface.Name}': {ex.Message}");
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    public async Task StartSubnetScanAsync()
    {
        if (SelectedInterface == null || !IsAuthorizedConfirmed)
            return;

        if (_subnetScanner == null)
        {
            ErrorMessage = "Subnet scanner service is not configured.";
            return;
        }

        IsDiscovering = true;
        ProgressPercentage = 0;
        ProgressStep = $"Initializing directed subnet sweep across {SubnetCidr}...";
        DevicesFoundCount = 0;
        ErrorMessage = null;

        _subnetCts?.Dispose();
        _subnetCts = new CancellationTokenSource();

        await _auditService.LogAsync(
            AuditAction.SubnetScanStarted,
            SelectedInterface.IPv4Address,
            succeeded: true,
            $"Started directed subnet scan for '{SubnetCidr}' on interface '{SelectedInterface.Name}'");

        var request = new SubnetScanRequest
        {
            TargetInterface = SelectedInterface,
            CidrOrSubnet = SubnetCidr,
            TimeoutPerHost = TimeSpan.FromMilliseconds(Math.Clamp(SubnetTimeoutMs, 100, 3000)),
            MaxConcurrency = Math.Clamp(SubnetConcurrency, 1, 32),
            ProbePort = SubnetProbePort > 0 ? SubnetProbePort : 3702
        };

        var progress = new Progress<DiscoveryProgressReport>(report =>
        {
            ProgressPercentage = report.Percentage;
            ProgressStep = report.CurrentStep;
            DevicesFoundCount = report.DevicesFound;

            if (report.LatestDevice != null)
            {
                AddOrUpdateDiscovered(report.LatestDevice);
            }
        });

        try
        {
            var results = await _subnetScanner.ScanSubnetAsync(request, progress, _subnetCts.Token);
            foreach (var dev in results)
            {
                AddOrUpdateDiscovered(dev);
            }

            ProgressStep = $"Subnet scan completed. Found {results.Count} devices.";
            await _auditService.LogAsync(
                AuditAction.SubnetScanCompleted,
                SelectedInterface.IPv4Address,
                succeeded: true,
                $"Subnet scan completed across '{SubnetCidr}'. Discovered {results.Count} cameras.");
        }
        catch (OperationCanceledException)
        {
            ProgressStep = "Subnet scan cancelled by user.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Subnet scan error: {ex.Message}";
            ProgressStep = "Subnet scan failed.";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    public async Task AddManualCameraAsync()
    {
        if (_deviceService == null)
        {
            ManualAddStatus = "Device management service not available.";
            return;
        }

        ManualAddStatus = "Adding camera to inventory...";
        try
        {
            CameraDevice dev;
            if (ManualProtocolIndex == 0) // ONVIF
            {
                var host = ManualHostOrUrl.Trim();
                if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(host, UriKind.Absolute, out var u))
                    {
                        host = u.Host;
                        if (u.Port > 0) ManualPort = u.Port;
                    }
                }

                dev = await _deviceService.AddManualDeviceAsync(
                    host,
                    ManualPort,
                    ManualPath,
                    string.IsNullOrWhiteSpace(ManualUsername) ? null : ManualUsername.Trim(),
                    string.IsNullOrWhiteSpace(ManualPassword) ? null : ManualPassword);
            }
            else // Direct RTSP
            {
                dev = await _deviceService.AddManualRtspDeviceAsync(
                    ManualCameraName,
                    ManualHostOrUrl.Trim(),
                    string.IsNullOrWhiteSpace(ManualUsername) ? null : ManualUsername.Trim(),
                    string.IsNullOrWhiteSpace(ManualPassword) ? null : ManualPassword,
                    string.IsNullOrWhiteSpace(ManualLocation) ? null : ManualLocation.Trim(),
                    ManualRtspTransport);
            }

            if (SelectedInterface != null)
            {
                dev.DiscoveryInterfaceName = SelectedInterface.Name;
                dev.Tags.Add($"interface:{SelectedInterface.Name}");
            }

            AddOrUpdateDiscovered(dev);
            ManualAddStatus = $"Successfully added '{dev.DisplayName}' ({dev.Host}) to inventory.";
        }
        catch (Exception ex)
        {
            ManualAddStatus = $"Error adding camera: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task TestManualConnectionAsync()
    {
        if (_deviceService == null) return;

        IsTestingConnection = true;
        ConnectionTestResultText = "Testing connection...";

        try
        {
            CameraDevice probeDevice;
            if (ManualProtocolIndex == 0)
            {
                probeDevice = new CameraDevice
                {
                    Host = ManualHostOrUrl.Trim(),
                    Port = ManualPort,
                    PrimaryServiceAddress = $"http://{ManualHostOrUrl.Trim()}:{ManualPort}{ManualPath}",
                    Protocol = CameraProtocolType.Onvif
                };
            }
            else
            {
                var host = "127.0.0.1";
                int port = 554;
                if (Uri.TryCreate(ManualHostOrUrl, UriKind.Absolute, out var u))
                {
                    host = u.Host;
                    port = u.Port > 0 ? u.Port : 554;
                }
                probeDevice = new CameraDevice
                {
                    Host = host,
                    Port = port,
                    RtspStreamUri = ManualHostOrUrl.Trim(),
                    Protocol = CameraProtocolType.RTSP
                };
            }

            var testResult = await _deviceService.TestDeviceConnectionAsync(probeDevice);
            ConnectionTestResultText = testResult.IsReachable
                ? $"Connection Successful: {testResult.StatusMessage}"
                : $"Connection Failed: {testResult.StatusMessage}";
        }
        catch (Exception ex)
        {
            ConnectionTestResultText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    public void CancelDiscovery()
    {
        if (!IsDiscovering) return;
        _subnetCts?.Cancel();
        var isDemo = _isDemoModeFunc();
        var engine = isDemo ? _demoEngine : _realEngine;
        engine.Cancel();
    }

    [RelayCommand]
    public async Task SaveAllToInventoryAsync()
    {
        foreach (var dev in DiscoveredDevices)
        {
            await _deviceRepository.AddOrUpdateAsync(dev);
        }
        ProgressStep = $"Saved {DiscoveredDevices.Count} devices to inventory.";
    }

    private void AddOrUpdateDiscovered(CameraDevice dev)
    {
        if (SelectedInterface != null && string.IsNullOrEmpty(dev.DiscoveryInterfaceName))
        {
            dev.DiscoveryInterfaceName = SelectedInterface.Name;
            if (!dev.Tags.Contains($"interface:{SelectedInterface.Name}"))
            {
                dev.Tags.Add($"interface:{SelectedInterface.Name}");
            }
        }

        var existing = DiscoveredDevices.FirstOrDefault(d =>
            d.EndpointId == dev.EndpointId ||
            (!string.IsNullOrEmpty(d.Host) && d.Host == dev.Host && d.Port == dev.Port));

        if (existing == null)
        {
            DiscoveredDevices.Add(dev);
        }
        else
        {
            var idx = DiscoveredDevices.IndexOf(existing);
            DiscoveredDevices[idx] = dev;
        }
    }

    private static string CalculateDefaultCidr(string? ipStr, string? maskStr)
    {
        if (string.IsNullOrWhiteSpace(ipStr) || !IPAddress.TryParse(ipStr, out var ip))
            return "192.168.1.0/24";

        var mask = IPAddress.TryParse(maskStr, out var m) ? m : IPAddress.Parse("255.255.255.0");
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var networkBytes = new byte[4];
        int prefixLength = 0;
        for (int i = 0; i < 4; i++)
        {
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            prefixLength += System.Numerics.BitOperations.PopCount(maskBytes[i]);
        }
        var networkIp = new IPAddress(networkBytes);
        return $"{networkIp}/{prefixLength}";
    }
}
