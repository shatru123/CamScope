using System.Collections.ObjectModel;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class RecordingsViewModel : ViewModelBase
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IOnvifClient _onvifClient;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<bool> _isDemoModeFunc;

    public ObservableCollection<CameraDevice> AvailableDevices { get; } = [];
    public ObservableCollection<RecordingSegment> Segments { get; } = [];

    [ObservableProperty] private CameraDevice? _selectedDevice;
    [ObservableProperty] private DateTimeOffset _searchDate = DateTimeOffset.UtcNow;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isUnsupported;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private RecordingSegment? _selectedSegment;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playheadPosition; // 0 to 100%
    [ObservableProperty] private string _playbackTimeText = "00:00:00";

    public RecordingsViewModel(
        IDeviceRepository deviceRepository,
        IOnvifClient onvifClient,
        ICredentialStore credentialStore,
        Func<bool> isDemoModeFunc)
    {
        _deviceRepository = deviceRepository;
        _onvifClient = onvifClient;
        _credentialStore = credentialStore;
        _isDemoModeFunc = isDemoModeFunc;
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
    {
        var devices = await _deviceRepository.GetAllAsync();
        AvailableDevices.Clear();
        foreach (var d in devices.Where(d => d.AuthState == AuthenticationState.Authenticated))
        {
            AvailableDevices.Add(d);
        }

        if (SelectedDevice == null)
        {
            SelectedDevice = AvailableDevices.FirstOrDefault();
        }
    }

    partial void OnSelectedDeviceChanged(CameraDevice? value)
    {
        Segments.Clear();
        SelectedSegment = null;
        IsPlaying = false;
        PlayheadPosition = 0;

        if (value == null)
        {
            IsUnsupported = false;
            StatusMessage = "Select an authenticated camera to search recordings.";
            return;
        }

        if (value.Capabilities.RecordingSearch == CapabilityStatus.Unsupported)
        {
            IsUnsupported = true;
            StatusMessage = "Playback Unavailable: This device does not expose the ONVIF Recording Search API.";
        }
        else
        {
            IsUnsupported = false;
            StatusMessage = "Ready to search recording timeline.";
        }
    }

    [RelayCommand]
    public async Task SearchRecordingsAsync()
    {
        if (SelectedDevice == null) return;

        if (SelectedDevice.Capabilities.RecordingSearch == CapabilityStatus.Unsupported)
        {
            IsUnsupported = true;
            StatusMessage = "Playback Unavailable: This device does not expose the ONVIF Recording Search API.";
            return;
        }

        IsSearching = true;
        StatusMessage = "Searching recording index...";
        Segments.Clear();

        try
        {
            if (SelectedDevice.IsDemoDevice || _isDemoModeFunc())
            {
                await Task.Delay(500);
                var demoSegs = DemoDataGenerator.CreateDemoRecordings(SelectedDevice.EndpointId);
                foreach (var seg in demoSegs)
                {
                    Segments.Add(seg);
                }
                StatusMessage = $"Found {demoSegs.Count} recording segments for {SearchDate:yyyy-MM-dd}.";
                SelectedSegment = Segments.FirstOrDefault();
                return;
            }

            var creds = await _credentialStore.GetCredentialsAsync(SelectedDevice.EndpointId);
            var serviceAddress = SelectedDevice.PrimaryServiceAddress
                ?? $"http://{SelectedDevice.Host}:{SelectedDevice.Port}/onvif/device_service";

            var startUtc = SearchDate.Date.ToUniversalTime();
            var endUtc = startUtc.AddDays(1);

            var list = await _onvifClient.FindRecordingsAsync(
                serviceAddress,
                startUtc,
                endUtc,
                creds?.Username,
                creds?.Password);

            if (list.Count == 0)
            {
                StatusMessage = "No recordings found for the specified date.";
            }
            else
            {
                foreach (var seg in list)
                {
                    Segments.Add(seg);
                }
                StatusMessage = $"Found {list.Count} recording segments.";
                SelectedSegment = Segments.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Recording search error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void Play()
    {
        if (SelectedSegment == null) return;
        IsPlaying = true;
        StatusMessage = $"Playing recording segment: {SelectedSegment.TrackName ?? SelectedSegment.Id}";
    }

    [RelayCommand]
    public void Pause()
    {
        IsPlaying = false;
        StatusMessage = "Playback paused.";
    }

    [RelayCommand]
    public void Seek(double position)
    {
        PlayheadPosition = Math.Clamp(position, 0, 100);
        var totalSeconds = SelectedSegment != null ? SelectedSegment.Duration.TotalSeconds : 3600;
        var currentSec = (int)(totalSeconds * (PlayheadPosition / 100.0));
        var ts = TimeSpan.FromSeconds(currentSec);
        PlaybackTimeText = ts.ToString(@"hh\:mm\:ss");
    }
}
