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

public sealed partial class LiveCellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IStreamingService _streamingService;
    private readonly DispatcherTimer? _clockTimer;
    private IStreamingSession? _session;
    private int _isUpdatingFrame;
    private bool _isDisposed;

    [ObservableProperty] private CameraDevice? _device;
    [ObservableProperty] private StreamState _state = StreamState.Idle;
    [ObservableProperty] private string? _statusText = "Idle - Select an authenticated camera";
    [ObservableProperty] private Bitmap? _currentBitmap;
    [ObservableProperty] private double _fps;
    [ObservableProperty] private int _frameCount;
    [ObservableProperty] private string _resolution = "1280x720";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isFullscreen;

    // Tactical CCTV OSD Properties
    [ObservableProperty] private string _cameraOsdTag = "";
    [ObservableProperty] private string _locationOsdTag = "";
    [ObservableProperty] private string _liveClockText = "";
    [ObservableProperty] private string _protocolText = "SIMULATED / DEMO";
    [ObservableProperty] private bool _isDemoDevice;
    [ObservableProperty] private bool _isUnauthenticated;
    [ObservableProperty] private bool _isSimulatedFeed;

    public ObservableCollection<CameraDevice> AvailableCameras { get; }

    public LiveCellViewModel(IStreamingService streamingService, ObservableCollection<CameraDevice> availableCameras)
    {
        _streamingService = streamingService;
        AvailableCameras = availableCameras;

        LiveClockText = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC");

        if (Avalonia.Application.Current != null)
        {
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _clockTimer.Tick += (_, _) =>
            {
                LiveClockText = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC");
            };
            _clockTimer.Start();
        }
    }

    partial void OnDeviceChanged(CameraDevice? oldValue, CameraDevice? newValue)
    {
        if (oldValue != null && IsStreaming)
        {
            _ = StopAsync();
        }

        ErrorMessage = null;

        if (newValue != null)
        {
            CameraOsdTag = $"[CAM - {newValue.DisplayName.ToUpperInvariant()}]";
            LocationOsdTag = !string.IsNullOrWhiteSpace(newValue.Location) ? $"LOC: {newValue.Location.ToUpperInvariant()}" : "LOC: UNASSIGNED";
            ProtocolText = newValue.IsDemoDevice ? "ONVIF PROFILE S [DEMO]" : (newValue.Protocol.ToString().ToUpperInvariant() + " / RTSP");
            IsDemoDevice = newValue.IsDemoDevice;
            IsUnauthenticated = newValue.AuthState == AuthenticationState.Unauthenticated;
            IsSimulatedFeed = newValue.IsDemoDevice;

            StatusText = newValue.AuthState == AuthenticationState.Authenticated
                ? (newValue.IsDemoDevice ? "Sample preview active - Click Start to stream" : "Ready to stream")
                : "Camera must be authenticated before viewing live stream";

            if (newValue.IsDemoDevice && !IsStreaming && Avalonia.Application.Current != null)
            {
                LoadPosterFrame(newValue);
            }
        }
        else
        {
            CameraOsdTag = "";
            LocationOsdTag = "";
            ProtocolText = "SIMULATED / DEMO";
            IsDemoDevice = false;
            IsUnauthenticated = false;
            IsSimulatedFeed = false;
            StatusText = "Idle - Select an authenticated camera";
            CurrentBitmap?.Dispose();
            CurrentBitmap = null;
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
                var old = CurrentBitmap;
                CurrentBitmap = new Bitmap(fs);
                old?.Dispose();
                Resolution = mapping.Resolution;
                return;
            }

            var frameBytes = SyntheticFrameGenerator.GenerateBmpFrame(device.DisplayName, device.Host, 0);
            using var ms = new MemoryStream(frameBytes);
            var oldBmp = CurrentBitmap;
            CurrentBitmap = new Bitmap(ms);
            oldBmp?.Dispose();
        }
        catch
        {
            // Headless test runner fallback
        }
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (_isDisposed) return;
        if (Device == null) return;

        if (Device.AuthState != AuthenticationState.Authenticated)
        {
            ErrorMessage = "Device must be authenticated before viewing live stream.";
            StatusText = "Authentication required";
            State = StreamState.Error;
            return;
        }

        await StopAsync();

        ErrorMessage = null;
        _session = _streamingService.CreateSession(Device);
        _session.FrameReceived += OnFrameReceived;
        _session.StateChanged += OnStateChanged;

        IsStreaming = true;
        await _session.StartAsync();
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (_session != null)
        {
            _session.FrameReceived -= OnFrameReceived;
            _session.StateChanged -= OnStateChanged;
            await _session.StopAsync();
            await _session.DisposeAsync();
            _session = null;
        }

        IsStreaming = false;
        State = StreamState.Idle;
        StatusText = "Stream stopped.";

        if (Device != null && Device.IsDemoDevice && Avalonia.Application.Current != null)
        {
            LoadPosterFrame(Device);
        }
        else
        {
            CurrentBitmap?.Dispose();
            CurrentBitmap = null;
        }
    }

    private void OnFrameReceived(byte[] bmpBytes)
    {
        if (_isDisposed || !IsStreaming) return;

        // Coalesce frame updates to prevent dispatcher queue backlog under rapid video streams
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
                    if (_isDisposed || !IsStreaming)
                    {
                        bmp.Dispose();
                        return;
                    }

                    var old = CurrentBitmap;
                    CurrentBitmap = bmp;
                    old?.Dispose();

                    if (_session != null)
                    {
                        Fps = Math.Round(_session.CurrentFps, 1);
                        FrameCount = _session.FrameCount;
                        if (!string.IsNullOrEmpty(_session.StreamResolution))
                        {
                            Resolution = _session.StreamResolution;
                        }
                    }
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

    private void OnStateChanged(StreamState newState, string? msg)
    {
        if (_isDisposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed) return;

            State = newState;
            StatusText = msg ?? newState.ToString();
            IsStreaming = newState == StreamState.Streaming;
            if (newState == StreamState.Error)
            {
                ErrorMessage = msg;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _clockTimer?.Stop();
        await StopAsync();
    }
}

public sealed partial class LiveMonitoringViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IStreamingService _streamingService;
    private readonly Func<bool>? _isDemoModeFunc;

    [ObservableProperty] private int _gridLayout = 4; // 1, 4, 9
    [ObservableProperty] private int _gridColumns = 2;
    [ObservableProperty] private int _gridRows = 2;
    [ObservableProperty] private LiveCellViewModel? _selectedCell;

    public ObservableCollection<LiveCellViewModel> Cells { get; } = [];
    public ObservableCollection<CameraDevice> AuthenticatedCameras { get; } = [];
    public ObservableCollection<CameraDevice> AvailableCameras { get; } = [];

    public LiveMonitoringViewModel(
        IDeviceRepository deviceRepository,
        IStreamingService streamingService,
        Func<bool>? isDemoModeFunc = null)
    {
        _deviceRepository = deviceRepository;
        _streamingService = streamingService;
        _isDemoModeFunc = isDemoModeFunc;

        SetLayout(4); // Default to 2x2 grid
    }

    [RelayCommand]
    public async Task LoadCamerasAsync()
    {
        var all = await _deviceRepository.GetAllAsync();
        bool isDemo = _isDemoModeFunc?.Invoke() ?? true;

        // In demo mode or if repo has no authenticated demo devices, seed standard demo cameras
        if (isDemo && (all.Count == 0 || !all.Any(d => d.IsDemoDevice && d.AuthState == AuthenticationState.Authenticated)))
        {
            foreach (var demo in DemoDataGenerator.CreateDemoDevices())
            {
                await _deviceRepository.AddOrUpdateAsync(demo);
            }
            all = await _deviceRepository.GetAllAsync();
        }

        AuthenticatedCameras.Clear();
        AvailableCameras.Clear();

        foreach (var dev in all)
        {
            AvailableCameras.Add(dev);
            if (dev.AuthState == AuthenticationState.Authenticated)
            {
                AuthenticatedCameras.Add(dev);
            }
        }

        // Auto-assign authenticated cameras to unassigned cells
        var authList = AuthenticatedCameras.ToList();
        for (int i = 0; i < Cells.Count && i < authList.Count; i++)
        {
            if (Cells[i].Device == null || !authList.Contains(Cells[i].Device!))
            {
                Cells[i].Device = authList[i];
            }
        }

        // In Demo Mode, auto-start all assigned cells so the user immediately sees live video streams
        if (isDemo)
        {
            foreach (var cell in Cells)
            {
                if (cell.Device != null && !cell.IsStreaming && cell.Device.AuthState == AuthenticationState.Authenticated)
                {
                    _ = cell.StartAsync();
                }
            }
        }
    }

    [RelayCommand]
    public void SetLayout(int cellCount)
    {
        GridLayout = cellCount;
        switch (cellCount)
        {
            case 1:
                GridColumns = 1;
                GridRows = 1;
                break;
            case 9:
                GridColumns = 3;
                GridRows = 3;
                break;
            default:
                GridColumns = 2;
                GridRows = 2;
                break;
        }

        while (Cells.Count < cellCount)
        {
            var cell = new LiveCellViewModel(_streamingService, AvailableCameras);
            Cells.Add(cell);
        }
        while (Cells.Count > cellCount)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            _ = cell.DisposeAsync();
        }

        var authList = AuthenticatedCameras.ToList();
        for (int i = 0; i < Cells.Count && i < authList.Count; i++)
        {
            if (Cells[i].Device == null)
            {
                Cells[i].Device = authList[i];
                if ((_isDemoModeFunc?.Invoke() ?? false) && !Cells[i].IsStreaming && Cells[i].Device?.AuthState == AuthenticationState.Authenticated)
                {
                    _ = Cells[i].StartAsync();
                }
            }
        }

        SelectedCell = Cells.FirstOrDefault();
    }

    [RelayCommand]
    public async Task StartAllAsync()
    {
        foreach (var cell in Cells)
        {
            if (cell.Device != null && !cell.IsStreaming)
            {
                await cell.StartAsync();
            }
        }
    }

    [RelayCommand]
    public async Task StopAllAsync()
    {
        foreach (var cell in Cells)
        {
            await cell.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cell in Cells)
        {
            await cell.DisposeAsync();
        }
        Cells.Clear();
    }
}
