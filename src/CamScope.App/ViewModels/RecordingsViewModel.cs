using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Demo;
using CamScope.Streaming.Rendering;
using CamScope.Streaming.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class RecordingsViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IOnvifClient _onvifClient;
    private readonly ICredentialStore _credentialStore;
    private readonly IStreamingService? _streamingService;
    private readonly Func<bool> _isDemoModeFunc;

    private IStreamingSession? _playbackSession;
    private int _isUpdatingFrame;
    private bool _isDisposed;

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
    [ObservableProperty] private Bitmap? _playbackBitmap;
    [ObservableProperty] private bool _isDemoRecording;
    [ObservableProperty] private string _segmentOsdTag = "";

    public RecordingsViewModel(
        IDeviceRepository deviceRepository,
        IOnvifClient onvifClient,
        ICredentialStore credentialStore,
        Func<bool> isDemoModeFunc,
        IStreamingService? streamingService = null)
    {
        _deviceRepository = deviceRepository;
        _onvifClient = onvifClient;
        _credentialStore = credentialStore;
        _isDemoModeFunc = isDemoModeFunc;
        _streamingService = streamingService;
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
        _ = StopPlaybackAsync();
        PlayheadPosition = 0;

        if (value == null)
        {
            IsUnsupported = false;
            StatusMessage = "Select an authenticated camera to search recordings.";
            PlaybackBitmap?.Dispose();
            PlaybackBitmap = null;
            return;
        }

        IsDemoRecording = value.IsDemoDevice || _isDemoModeFunc();

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

        if (IsDemoRecording && Avalonia.Application.Current != null)
        {
            LoadPosterFrame(value);
        }
    }

    partial void OnSelectedSegmentChanged(RecordingSegment? value)
    {
        if (value != null)
        {
            SegmentOsdTag = $"ARCHIVE: {value.TrackName ?? value.Id} | {value.StartTimeUtc:yyyy-MM-dd HH:mm:ss UTC}";
            StatusMessage = $"Selected {value.TrackName ?? value.Id} (Duration: {value.Duration:hh\\:mm\\:ss})";
        }
        else
        {
            SegmentOsdTag = "";
        }
    }

    private void LoadPosterFrame(CameraDevice device)
    {
        try
        {
            var mapping = DemoCameraAssetCatalog.ResolveForDevice(device);
            var posterPath = DemoVideoPlaybackSession.ResolvePosterFilePath(mapping.PosterFileName);

            if (posterPath != null && File.Exists(posterPath))
            {
                using var fs = File.OpenRead(posterPath);
                var old = PlaybackBitmap;
                PlaybackBitmap = new Bitmap(fs);
                old?.Dispose();
                return;
            }

            var frameBytes = SyntheticFrameGenerator.GenerateBmpFrame(device.DisplayName, device.Host, 0);
            using var ms = new MemoryStream(frameBytes);
            var oldBmp = PlaybackBitmap;
            PlaybackBitmap = new Bitmap(ms);
            oldBmp?.Dispose();
        }
        catch
        {
            // Headless test runner fallback
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
                await Task.Delay(300);
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
    public async Task PlayAsync(RecordingSegment? segment = null)
    {
        if (segment != null)
        {
            SelectedSegment = segment;
        }

        if (SelectedSegment == null || SelectedDevice == null) return;

        await StopPlaybackAsync();

        IsPlaying = true;
        StatusMessage = $"Playing archive stream: {SelectedSegment.TrackName ?? SelectedSegment.Id}";

        if (_streamingService != null)
        {
            _playbackSession = _streamingService.CreateSession(SelectedDevice);
            _playbackSession.FrameReceived += OnPlaybackFrameReceived;
            await _playbackSession.StartAsync();
        }
    }

    [RelayCommand]
    public async Task PauseAsync()
    {
        await StopPlaybackAsync();
        StatusMessage = "Playback paused.";
    }

    private async Task StopPlaybackAsync()
    {
        IsPlaying = false;
        if (_playbackSession != null)
        {
            _playbackSession.FrameReceived -= OnPlaybackFrameReceived;
            await _playbackSession.StopAsync();
            await _playbackSession.DisposeAsync();
            _playbackSession = null;
        }
    }

    private void OnPlaybackFrameReceived(byte[] bmpBytes)
    {
        if (_isDisposed || !IsPlaying) return;

        if (Interlocked.CompareExchange(ref _isUpdatingFrame, 1, 0) != 0)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream(bmpBytes);
            var bmp = new Bitmap(ms);

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_isDisposed || !IsPlaying)
                    {
                        bmp.Dispose();
                        return;
                    }

                    var old = PlaybackBitmap;
                    PlaybackBitmap = bmp;
                    old?.Dispose();
                }
                finally
                {
                    Interlocked.Exchange(ref _isUpdatingFrame, 0);
                }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _isUpdatingFrame, 0);
        }
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

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopPlaybackAsync();
        PlaybackBitmap?.Dispose();
        PlaybackBitmap = null;
    }
}
