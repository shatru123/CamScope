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
    private readonly DeviceManagementService? _deviceService;

    [ObservableProperty] private bool _isDemoDevice;
    [ObservableProperty] private bool _isUnauthenticated;
    [ObservableProperty] private bool _isSimulatedFeed;
    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string? _testResultText;
    [ObservableProperty] private string _transportBadgeText = "RTSP/TCP";

    public string ConnectionStatusBadge
    {
        get
        {
            if (Device == null) return "NO CAMERA";
            if (IsDemoDevice) return "DEMO";
            if (IsUnauthenticated) return "AUTHENTICATION REQUIRED";
            if (State == StreamState.Streaming) return "REAL LIVE FEED";
            if (State == StreamState.Connecting || State == StreamState.Reconnecting) return "CONNECTING";
            if (State == StreamState.Error || Device.ConnectionState == DeviceConnectionState.Unavailable) return "OFFLINE";
            return "READY";
        }
    }

    public bool IsRealLiveFeed => State == StreamState.Streaming && !IsDemoDevice;
    public bool IsConnecting => (State == StreamState.Connecting || State == StreamState.Reconnecting) && !IsDemoDevice;
    public bool IsOffline => State == StreamState.Error || (Device != null && Device.ConnectionState == DeviceConnectionState.Unavailable);

    public void NotifyStatusProperties()
    {
        OnPropertyChanged(nameof(ConnectionStatusBadge));
        OnPropertyChanged(nameof(IsRealLiveFeed));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsOffline));
    }

    public ObservableCollection<CameraDevice> AvailableCameras { get; }

    public LiveCellViewModel(
        IStreamingService streamingService,
        ObservableCollection<CameraDevice> availableCameras,
        DeviceManagementService? deviceService = null)
    {
        _streamingService = streamingService;
        AvailableCameras = availableCameras;
        _deviceService = deviceService;

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

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (Device == null) return;
        IsTestingConnection = true;
        TestResultText = "Probing...";

        try
        {
            if (_deviceService != null)
            {
                var result = await _deviceService.TestDeviceConnectionAsync(Device);
                TestResultText = result.IsReachable
                    ? $"Reachable ({result.LatencyMs:F1}ms)"
                    : $"Unreachable: {result.StatusMessage}";
                NotifyStatusProperties();
            }
            else
            {
                TestResultText = "Service not available";
            }
        }
        catch (Exception ex)
        {
            TestResultText = $"Probe failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
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

            TransportBadgeText = !string.IsNullOrWhiteSpace(newValue.RtspTransport)
                ? $"RTSP/{newValue.RtspTransport.ToUpperInvariant()}"
                : "RTSP/TCP";
            NotifyStatusProperties();
        }
        else
        {
            CameraOsdTag = "";
            LocationOsdTag = "";
            ProtocolText = "SIMULATED / DEMO";
            IsDemoDevice = false;
            IsUnauthenticated = false;
            IsSimulatedFeed = false;
            TransportBadgeText = "RTSP/TCP";
            StatusText = "Idle - Select an authenticated camera";
            CurrentBitmap?.Dispose();
            CurrentBitmap = null;
            NotifyStatusProperties();
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
            NotifyStatusProperties();
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
        NotifyStatusProperties();

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
            NotifyStatusProperties();
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
    private readonly INetworkSelectionService? _selectionService;
    private readonly INetworkInterfaceProvider? _interfaceProvider;
    private readonly DeviceManagementService? _deviceService;
    private readonly Action? _navigateToDiscovery;

    [ObservableProperty] private int _gridLayout = 4; // 1, 4, 9
    [ObservableProperty] private int _gridColumns = 2;
    [ObservableProperty] private int _gridRows = 2;
    [ObservableProperty] private LiveCellViewModel? _selectedCell;

    // Interface Selector
    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;
    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];

    // Quick Add Modal Dialog
    [ObservableProperty] private bool _isQuickAddDialogOpen;
    [ObservableProperty] private int _quickAddProtocolIndex = 0; // 0: ONVIF, 1: RTSP
    [ObservableProperty] private string _quickAddName = "Camera";
    [ObservableProperty] private string _quickAddHostOrUrl = "192.168.1.100";
    [ObservableProperty] private int _quickAddPort = 80;
    [ObservableProperty] private string _quickAddPath = "/onvif/device_service";
    [ObservableProperty] private string _quickAddUsername = "";
    [ObservableProperty] private string _quickAddPassword = "";
    [ObservableProperty] private string _quickAddLocation = "";
    [ObservableProperty] private string _quickAddRtspTransport = "tcp";
    [ObservableProperty] private string? _quickAddError;
    [ObservableProperty] private bool _isQuickAdding;

    public ObservableCollection<LiveCellViewModel> Cells { get; } = [];
    public ObservableCollection<CameraDevice> AuthenticatedCameras { get; } = [];
    public ObservableCollection<CameraDevice> AvailableCameras { get; } = [];

    public LiveMonitoringViewModel(
        IDeviceRepository deviceRepository,
        IStreamingService streamingService,
        Func<bool>? isDemoModeFunc = null,
        INetworkSelectionService? selectionService = null,
        INetworkInterfaceProvider? interfaceProvider = null,
        DeviceManagementService? deviceService = null,
        Action? navigateToDiscovery = null)
    {
        _deviceRepository = deviceRepository;
        _streamingService = streamingService;
        _isDemoModeFunc = isDemoModeFunc;
        _selectionService = selectionService;
        _interfaceProvider = interfaceProvider;
        _deviceService = deviceService;
        _navigateToDiscovery = navigateToDiscovery;

        if (_selectionService != null)
        {
            _selectionService.SelectionChanged += OnExternalSelectionChanged;
        }

        if (_interfaceProvider != null)
        {
            try
            {
                _interfaceProvider.InterfacesChanged += () =>
                {
                    Dispatcher.UIThread.Post(LoadInterfaces);
                };
            }
            catch
            {
                // Ignore mock event registration
            }
        }

        LoadInterfaces();
        SetLayout(4); // Default to 2x2 grid
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
        if (_interfaceProvider != null)
        {
            var ifaces = _interfaceProvider.GetEligibleInterfaces(includeLoopback: false);
            foreach (var i in ifaces)
            {
                Interfaces.Add(i);
            }
        }

        var currentSelected = _selectionService?.CurrentScanningInterface;
        if (currentSelected != null)
        {
            SelectedInterface = Interfaces.FirstOrDefault(i => i.Id == currentSelected.Id || i.IPv4Address == currentSelected.IPv4Address);
        }

        SelectedInterface ??= Interfaces.FirstOrDefault();
    }

    partial void OnSelectedInterfaceChanged(NetworkInterfaceInfo? value)
    {
        if (value != null)
        {
            _selectionService?.SelectScanningInterface(value);
        }
    }

    partial void OnQuickAddProtocolIndexChanged(int value)
    {
        if (value == 0) // ONVIF
        {
            QuickAddPort = 80;
            QuickAddHostOrUrl = "192.168.1.100";
        }
        else // RTSP
        {
            QuickAddPort = 554;
            QuickAddHostOrUrl = "rtsp://192.168.1.100:554/stream1";
        }
        OnPropertyChanged(nameof(IsQuickAddOnvif));
        OnPropertyChanged(nameof(IsQuickAddRtsp));
    }

    public bool IsQuickAddOnvif => QuickAddProtocolIndex == 0;
    public bool IsQuickAddRtsp => QuickAddProtocolIndex == 1;

    [RelayCommand]
    public void OpenQuickAddDialog()
    {
        QuickAddProtocolIndex = 0;
        QuickAddName = "Camera";
        QuickAddHostOrUrl = "192.168.1.100";
        QuickAddPort = 80;
        QuickAddPath = "/onvif/device_service";
        QuickAddUsername = "";
        QuickAddPassword = "";
        QuickAddLocation = "";
        QuickAddError = null;
        IsQuickAddDialogOpen = true;
    }

    [RelayCommand]
    public void CloseQuickAddDialog()
    {
        IsQuickAddDialogOpen = false;
        QuickAddError = null;
    }

    [RelayCommand]
    public async Task SubmitQuickAddCameraAsync()
    {
        if (_deviceService == null)
        {
            QuickAddError = "Device service unavailable.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QuickAddHostOrUrl))
        {
            QuickAddError = "Host / URL is required.";
            return;
        }

        IsQuickAdding = true;
        QuickAddError = null;

        try
        {
            CameraDevice dev;
            if (QuickAddProtocolIndex == 0) // ONVIF
            {
                var host = QuickAddHostOrUrl.Trim();
                if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(host, UriKind.Absolute, out var u))
                    {
                        host = u.Host;
                        if (u.Port > 0) QuickAddPort = u.Port;
                    }
                }

                dev = await _deviceService.AddManualDeviceAsync(
                    host,
                    QuickAddPort,
                    QuickAddPath,
                    string.IsNullOrWhiteSpace(QuickAddUsername) ? null : QuickAddUsername.Trim(),
                    string.IsNullOrWhiteSpace(QuickAddPassword) ? null : QuickAddPassword);
            }
            else // Direct RTSP
            {
                dev = await _deviceService.AddManualRtspDeviceAsync(
                    QuickAddName,
                    QuickAddHostOrUrl.Trim(),
                    string.IsNullOrWhiteSpace(QuickAddUsername) ? null : QuickAddUsername.Trim(),
                    string.IsNullOrWhiteSpace(QuickAddPassword) ? null : QuickAddPassword,
                    string.IsNullOrWhiteSpace(QuickAddLocation) ? null : QuickAddLocation.Trim(),
                    QuickAddRtspTransport);
            }

            if (SelectedInterface != null)
            {
                dev.DiscoveryInterfaceName = SelectedInterface.Name;
                dev.Tags.Add($"interface:{SelectedInterface.Name}");
            }

            IsQuickAddDialogOpen = false;
            await LoadCamerasAsync();

            // Assign to first empty cell
            var emptyCell = Cells.FirstOrDefault(c => c.Device == null);
            if (emptyCell != null)
            {
                emptyCell.Device = dev;
            }
        }
        catch (Exception ex)
        {
            QuickAddError = ex.Message;
        }
        finally
        {
            IsQuickAdding = false;
        }
    }

    [RelayCommand]
    public void TriggerDiscovery()
    {
        _navigateToDiscovery?.Invoke();
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
            var cell = new LiveCellViewModel(_streamingService, AvailableCameras, _deviceService);
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
