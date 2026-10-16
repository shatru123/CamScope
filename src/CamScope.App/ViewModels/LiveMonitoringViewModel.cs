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

    public async Task StartAsync()
    {
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
        try
        {
            using var ms = new MemoryStream(bmpBytes);
            var bmp = new Bitmap(ms);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var old = CurrentBitmap;
                CurrentBitmap = bmp;
                old?.Dispose();

                if (_session != null)
                {
                    Fps = Math.Round(_session.CurrentFps, 1);
                    FrameCount = _session.FrameCount;
                }
            });
        }
        catch
        {
            // Ignore decoding glitches during rapid frame updates
        }
    }

    private void OnStateChanged(StreamState newState, string? msg)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
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
        await StopAsync();
    }
}

public sealed partial class LiveMonitoringViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IStreamingService _streamingService;

    [ObservableProperty] private int _gridLayout = 4; // 1, 4, 9
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
        while (Cells.Count < cellCount)
        {
            var cell = new LiveCellViewModel(_streamingService, AuthenticatedCameras);
            Cells.Add(cell);
        }
        while (Cells.Count > cellCount)
        {
            var cell = Cells[^1];
            _ = cell.StopAsync();
            Cells.RemoveAt(Cells.Count - 1);
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
    }
}
