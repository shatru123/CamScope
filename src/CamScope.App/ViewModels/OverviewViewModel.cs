using System.Collections.ObjectModel;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class OverviewViewModel : ViewModelBase
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IAuditService _auditService;
    private readonly Action<int> _navigateToIndex;

    [ObservableProperty] private int _totalDevices;
    [ObservableProperty] private int _authenticatedDevices;
    [ObservableProperty] private int _reachableDevices;
    [ObservableProperty] private int _unreachableDevices;
    [ObservableProperty] private int _liveStreamCapableDevices;
    [ObservableProperty] private int _recordingCapableDevices;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<CameraDevice> RecentDevices { get; } = [];
    public ObservableCollection<ActivityAuditEntry> RecentEvents { get; } = [];

    public OverviewViewModel(
        IDeviceRepository deviceRepository,
        IAuditService auditService,
        Action<int> navigateToIndex)
    {
        _deviceRepository = deviceRepository;
        _auditService = auditService;
        _navigateToIndex = navigateToIndex;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var devices = await _deviceRepository.GetAllAsync();
            TotalDevices = devices.Count;
            AuthenticatedDevices = devices.Count(d => d.AuthState == AuthenticationState.Authenticated);
            ReachableDevices = devices.Count(d => d.ConnectionState == DeviceConnectionState.Connected ||
                                                 d.ConnectionState == DeviceConnectionState.Reachable ||
                                                 d.ConnectionState == DeviceConnectionState.Discovered);
            UnreachableDevices = devices.Count(d => d.ConnectionState == DeviceConnectionState.Unavailable ||
                                                   d.ConnectionState == DeviceConnectionState.Error);
            LiveStreamCapableDevices = devices.Count(d => d.Capabilities.LiveStreaming == CapabilityStatus.Supported);
            RecordingCapableDevices = devices.Count(d => d.Capabilities.RecordingSearch == CapabilityStatus.Supported);

            RecentDevices.Clear();
            foreach (var d in devices.Take(6))
            {
                RecentDevices.Add(d);
            }

            var logs = await _auditService.GetRecentLogsAsync(6);
            RecentEvents.Clear();
            foreach (var l in logs)
            {
                RecentEvents.Add(l);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void GoToDiscovery() => _navigateToIndex(1);

    [RelayCommand]
    public void GoToLiveMonitoring() => _navigateToIndex(3);

    [RelayCommand]
    public void GoToInventory() => _navigateToIndex(2);
}
