using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class LiveCellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IStreamingService _streamingService;
    private IStreamingSession? _session;
    private int _isUpdatingFrame;
    private bool _isDisposed;

    [ObservableProperty] private CameraDevice? _device;
    [ObservableProperty] private StreamState _state = StreamState.Idle;
    [ObservableProperty] private string? _statusText = "Idle - Select an authenticated camera";
    [ObservableProperty] private Bitmap? _currentBitmap;
    [ObservableProperty] private double _fps;
    [ObservableProperty] private int _frameCount;
    [ObservableProperty] private string _resolution = "1920x1080";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isFullscreen;

    public ObservableCollection<CameraDevice> AvailableCameras { get; }

    public LiveCellViewModel(IStreamingService streamingService, ObservableCollection<CameraDevice> availableCameras)
    {
        _streamingService = streamingService;
        AvailableCameras = availableCameras;
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
            StatusText = newValue.AuthState == AuthenticationState.Authenticated
                ? "Ready to stream"
                : "Camera must be authenticated before viewing live stream";
        }
        else
        {
            StatusText = "Idle - Select an authenticated camera";
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
        CurrentBitmap?.Dispose();
        CurrentBitmap = null;
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

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

        await StopAsync();
    }
}

public sealed partial class LiveMonitoringViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IStreamingService _streamingService;

    [ObservableProperty] private int _gridLayout = 4; // 1, 4, 9
    [ObservableProperty] private int _gridColumns = 2;
    [ObservableProperty] private int _gridRows = 2;
    [ObservableProperty] private LiveCellViewModel? _selectedCell;

    public ObservableCollection<LiveCellViewModel> Cells { get; } = [];
    public ObservableCollection<CameraDevice> AuthenticatedCameras { get; } = [];

    public LiveMonitoringViewModel(
        IDeviceRepository deviceRepository,
        IStreamingService streamingService)
    {
        _deviceRepository = deviceRepository;
        _streamingService = streamingService;

        SetLayout(4); // Default to 2x2 grid
    }

    [RelayCommand]
    public async Task LoadCamerasAsync()
    {
        var all = await _deviceRepository.GetAllAsync();
        AuthenticatedCameras.Clear();
        foreach (var dev in all.Where(d => d.AuthState == AuthenticationState.Authenticated))
        {
            AuthenticatedCameras.Add(dev);
        }

        // Auto-assign authenticated cameras to unassigned cells
        var authList = AuthenticatedCameras.ToList();
        for (int i = 0; i < Cells.Count && i < authList.Count; i++)
        {
            if (Cells[i].Device == null)
            {
                Cells[i].Device = authList[i];
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
            var cell = new LiveCellViewModel(_streamingService, AuthenticatedCameras);
            Cells.Add(cell);
        }
        while (Cells.Count > cellCount)
        {
            var cell = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            _ = cell.DisposeAsync();
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
