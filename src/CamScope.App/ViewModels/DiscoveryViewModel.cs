using System.Collections.ObjectModel;
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

    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;
    [ObservableProperty] private bool _isAuthorizedConfirmed;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private int _progressPercentage;
    [ObservableProperty] private string _progressStep = "Ready to discover.";
    [ObservableProperty] private int _devicesFoundCount;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];
    public ObservableCollection<CameraDevice> DiscoveredDevices { get; } = [];

    public bool CanStartDiscovery => SelectedInterface != null && IsAuthorizedConfirmed && !IsDiscovering;

    public DiscoveryViewModel(
        INetworkDiscoveryEngine realEngine,
        INetworkDiscoveryEngine demoEngine,
        INetworkInterfaceProvider interfaceProvider,
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        Func<bool> isDemoModeFunc)
    {
        _realEngine = realEngine;
        _demoEngine = demoEngine;
        _interfaceProvider = interfaceProvider;
        _deviceRepository = deviceRepository;
        _auditService = auditService;
        _isDemoModeFunc = isDemoModeFunc;

        LoadInterfaces();
    }

    public void LoadInterfaces()
    {
        Interfaces.Clear();
        var ifaces = _interfaceProvider.GetEligibleInterfaces();
        foreach (var i in ifaces)
        {
            Interfaces.Add(i);
        }
        SelectedInterface = Interfaces.FirstOrDefault();
    }

    partial void OnSelectedInterfaceChanged(NetworkInterfaceInfo? value) => OnPropertyChanged(nameof(CanStartDiscovery));
    partial void OnIsAuthorizedConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanStartDiscovery));
    partial void OnIsDiscoveringChanged(bool value) => OnPropertyChanged(nameof(CanStartDiscovery));

    [RelayCommand]
    public async Task StartDiscoveryAsync()
    {
        if (SelectedInterface == null || !IsAuthorizedConfirmed)
            return;

        IsDiscovering = true;
        ProgressPercentage = 0;
        ProgressStep = "Preparing discovery...";
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

            if (report.LatestDevice != null && !DiscoveredDevices.Any(d => d.EndpointId == report.LatestDevice.EndpointId))
            {
                DiscoveredDevices.Add(report.LatestDevice);
            }
        });

        try
        {
            var results = await engine.DiscoverAsync(SelectedInterface, CancellationToken.None, progress);

            foreach (var dev in results)
            {
                if (!DiscoveredDevices.Any(d => d.EndpointId == dev.EndpointId))
                {
                    DiscoveredDevices.Add(dev);
                }
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
    public void CancelDiscovery()
    {
        if (!IsDiscovering) return;
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
}
