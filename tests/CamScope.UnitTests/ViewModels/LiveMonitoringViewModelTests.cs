using CamScope.App.ViewModels;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class LiveMonitoringViewModelTests
{
    private sealed class InMemoryDeviceRepository : IDeviceRepository
    {
        public List<CameraDevice> Devices { get; } = [];

        public Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CameraDevice>>(Devices);

        public Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id));

        public Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default)
            => Task.FromResult(Devices.FirstOrDefault(d => d.EndpointId == endpointId));

        public Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default)
        {
            var idx = Devices.FindIndex(d => d.EndpointId == device.EndpointId);
            if (idx >= 0) Devices[idx] = device;
            else Devices.Add(device);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Devices.RemoveAll(d => d.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken ct = default)
        {
            Devices.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LoadCamerasAsync_InDemoMode_SeedsCamerasAndAssignsCells()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true);

        Assert.Equal(4, vm.Cells.Count);
        Assert.Null(vm.Cells[0].Device);

        await vm.LoadCamerasAsync();

        // Must seed demo devices and populate authenticated cameras
        Assert.NotEmpty(repo.Devices);
        Assert.NotEmpty(vm.AuthenticatedCameras);

        // Cells 0-3 must be assigned to authenticated demo cameras
        for (int i = 0; i < 4; i++)
        {
            Assert.NotNull(vm.Cells[i].Device);
            Assert.Equal(AuthenticationState.Authenticated, vm.Cells[i].Device!.AuthState);
            Assert.True(vm.Cells[i].Device!.IsDemoDevice);
        }

        // Clean up
        await vm.DisposeAsync();
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(9, 3, 3)]
    public async Task SetLayout_AdjustsGridRowsColumnsAndCellCount(int targetCells, int expectedRows, int expectedCols)
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true);

        vm.SetLayout(targetCells);

        Assert.Equal(targetCells, vm.Cells.Count);
        Assert.Equal(expectedRows, vm.GridRows);
        Assert.Equal(expectedCols, vm.GridColumns);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StopAllAsync_StopsAllStreamingCells()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => false);

        await vm.LoadCamerasAsync();
        await vm.StopAllAsync();

        Assert.All(vm.Cells, cell => Assert.False(cell.IsStreaming));
        await vm.DisposeAsync();
    }

    private sealed class DummyOnvifClient : IOnvifClient
    {
        public Task<bool> ProbeReachableAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
        public Task<DeviceInformationResult> GetDeviceInformationAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult(new DeviceInformationResult("Vendor", "Model", "1.0", "123", "HW1"));
        public Task<DeviceCapabilities> GetCapabilitiesAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult(new DeviceCapabilities());
        public Task<string?> GetStreamUriAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult<string?>("rtsp://127.0.0.1/live");
        public Task<IReadOnlyList<RecordingSegment>> FindRecordingsAsync(string serviceAddress, DateTime startUtc, DateTime endUtc, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordingSegment>>([]);
        public Task<CameraConnectionTestResult> TestConnectionAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(new CameraConnectionTestResult(true, 10.0, true, true, null));
    }

    private sealed class DummyCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, (string user, string pass)> _creds = new();
        public Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default)
        {
            _creds[endpointId] = (username, password);
            return Task.CompletedTask;
        }
        public Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            if (_creds.TryGetValue(endpointId, out var c)) return Task.FromResult<(string?, string?)?>(c);
            return Task.FromResult<(string?, string?)?>(null);
        }
        public Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            _creds.Remove(endpointId);
            return Task.CompletedTask;
        }
    }

    private sealed class DummyAuditService : IAuditService
    {
        public Task LogAsync(AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int count = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>([]);
        public Task ClearLogsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummySelectionService : INetworkSelectionService
    {
        public NetworkInterfaceInfo? CurrentScanningInterface { get; private set; }
        public NetworkInterfaceInfo? CctvInterface { get; private set; }
        public WifiConnectionInfo? CurrentCctvWifiConnection { get; private set; }
        public event Action<NetworkInterfaceInfo?>? SelectionChanged;
        public event Action<NetworkInterfaceInfo?>? CctvInterfaceChanged;

        public void SelectScanningInterface(NetworkInterfaceInfo iface)
        {
            CurrentScanningInterface = iface;
            SelectionChanged?.Invoke(iface);
        }

        public void SetCctvInterface(NetworkInterfaceInfo? iface, WifiConnectionInfo? connection = null)
        {
            CctvInterface = iface;
            CurrentCctvWifiConnection = connection;
            CctvInterfaceChanged?.Invoke(iface);
        }
    }

    [Fact]
    public async Task SelectedInterface_UpdatesSelectionService()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var selectionService = new DummySelectionService();
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true, selectionService);

        var iface = new NetworkInterfaceInfo { Id = "eth0", Name = "eth0", IPv4Address = "192.168.1.50" };
        vm.Interfaces.Add(iface);
        vm.SelectedInterface = iface;

        Assert.Same(iface, selectionService.CurrentScanningInterface);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task QuickAddDialog_OpensClosesAndSwitchesProtocol()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true);

        Assert.False(vm.IsQuickAddDialogOpen);

        vm.OpenQuickAddDialog();
        Assert.True(vm.IsQuickAddDialogOpen);
        Assert.True(vm.IsQuickAddOnvif);
        Assert.False(vm.IsQuickAddRtsp);

        vm.QuickAddProtocolIndex = 1;
        Assert.False(vm.IsQuickAddOnvif);
        Assert.True(vm.IsQuickAddRtsp);
        Assert.Equal(554, vm.QuickAddPort);

        vm.CloseQuickAddDialog();
        Assert.False(vm.IsQuickAddDialogOpen);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SubmitQuickAddCameraAsync_AddsDirectRtspDeviceAndAssignsToFirstEmptyCell()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var onvifClient = new DummyOnvifClient();
        var credStore = new DummyCredentialStore();
        var audit = new DummyAuditService();
        var deviceService = new CamScope.Application.Services.DeviceManagementService(
            repo, credStore, onvifClient, audit, NullLogger<CamScope.Application.Services.DeviceManagementService>.Instance);

        var vm = new LiveMonitoringViewModel(repo, streamingService, () => false, deviceService: deviceService);

        vm.OpenQuickAddDialog();
        vm.QuickAddProtocolIndex = 1; // RTSP
        vm.QuickAddName = "Gate Camera";
        vm.QuickAddHostOrUrl = "rtsp://192.168.1.120:554/live";
        vm.QuickAddUsername = "admin";
        vm.QuickAddPassword = "secretpassword";
        vm.QuickAddLocation = "Perimeter Gate";
        vm.QuickAddRtspTransport = "udp";

        await vm.SubmitQuickAddCameraAsync();

        Assert.False(vm.IsQuickAddDialogOpen);
        Assert.Null(vm.QuickAddError);

        var added = repo.Devices.FirstOrDefault(d => d.DisplayName == "Gate Camera");
        Assert.NotNull(added);
        Assert.Equal("udp", added.RtspTransport);
        Assert.Equal("rtsp://192.168.1.120:554/live", added.RtspStreamUri);
        Assert.Equal(AuthenticationState.Authenticated, added.AuthState);

        // Assigned to first empty cell
        Assert.Same(added, vm.Cells[0].Device);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TriggerDiscovery_FiresCallback()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        bool navigated = false;
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true, navigateToDiscovery: () => navigated = true);

        vm.TriggerDiscovery();

        Assert.True(navigated);

        await vm.DisposeAsync();
    }
}
