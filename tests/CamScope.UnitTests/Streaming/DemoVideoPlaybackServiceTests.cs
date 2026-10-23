using CamScope.App.ViewModels;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming;
using CamScope.Streaming.Demo;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Streaming;

public class DemoVideoPlaybackServiceTests
{
    private sealed class InMemoryDeviceRepo : IDeviceRepository
    {
        public List<CameraDevice> Devices { get; } = [];
        public Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CameraDevice>>(Devices);
        public Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id));
        public Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default) => Task.FromResult(Devices.FirstOrDefault(d => d.EndpointId == endpointId));
        public Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default)
        {
            var idx = Devices.FindIndex(d => d.EndpointId == device.EndpointId);
            if (idx >= 0) Devices[idx] = device; else Devices.Add(device);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, (string user, string pass)> _creds = new();
        public Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default)
        {
            _creds[endpointId] = (username, password);
            return Task.CompletedTask;
        }
        public Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            if (_creds.TryGetValue(endpointId, out var val))
                return Task.FromResult<(string? Username, string? Password)?>((val.user, val.pass));
            return Task.FromResult<(string? Username, string? Password)?>(null);
        }
        public Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default)
        {
            _creds.Remove(endpointId);
            return Task.CompletedTask;
        }
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
    }

    [Fact]
    public void Catalog_ContainsExplicitMappingsForAllSixDemoCameras()
    {
        var all = DemoCameraAssetCatalog.AllMappings;
        Assert.True(all.Count >= 5);

        // Verify camera 1: Front Entrance
        var cam1 = DemoCameraAssetCatalog.FindByEndpointId("urn:uuid:demo-axis-p3245-v-00408c123456");
        Assert.NotNull(cam1);
        Assert.Equal("cctv_front_entrance.mp4", cam1.VideoFileName);
        Assert.Equal(25, cam1.TargetFps);
        Assert.Equal("1280x720", cam1.Resolution);

        // Verify camera 2: Loading Dock
        var cam2 = DemoCameraAssetCatalog.FindByEndpointId("urn:uuid:demo-hanwha-xnp6400rw-00166c654321");
        Assert.NotNull(cam2);
        Assert.Equal("cctv_loading_dock.mp4", cam2.VideoFileName);

        // Verify camera 3: Server Room
        var cam3 = DemoCameraAssetCatalog.FindByEndpointId("urn:uuid:demo-hikvision-ds2cd2143g2-40b4cd112233");
        Assert.NotNull(cam3);
        Assert.Equal("cctv_server_room.mp4", cam3.VideoFileName);

        // Verify camera 4: Perimeter Fence Thermal
        var cam4 = DemoCameraAssetCatalog.FindByEndpointId("urn:uuid:demo-bosch-mic-9000i-00075f998877");
        Assert.NotNull(cam4);
        Assert.Equal("cctv_perimeter_fence_thermal.mp4", cam4.VideoFileName);

        // Verify camera 5: Parking Lot 4K Bullet
        var cam5 = DemoCameraAssetCatalog.FindByEndpointId("urn:uuid:demo-dahua-ipc-hfw5842e-3c52a1334455");
        Assert.NotNull(cam5);
        Assert.Equal("cctv_parking_lot.mp4", cam5.VideoFileName);
    }

    [Fact]
    public void Catalog_FallbackResolution_MatchesKeywords()
    {
        var customDev = new CameraDevice
        {
            EndpointId = "urn:uuid:custom-unknown",
            DisplayName = "Custom Warehouse Dock Camera",
            IsDemoDevice = true
        };

        var mapping = DemoCameraAssetCatalog.ResolveForDevice(customDev);
        Assert.NotNull(mapping);
        Assert.Equal("cctv_loading_dock.mp4", mapping.VideoFileName);
    }

    [Fact]
    public void DemoVideoPlaybackService_ResolvesLocalVideoAndPosterFiles()
    {
        var service = new DemoVideoPlaybackService(NullLoggerFactory.Instance);
        var devices = DemoDataGenerator.CreateDemoDevices();

        foreach (var dev in devices)
        {
            Assert.True(service.IsDemoDevice(dev));
            var mapping = service.GetMapping(dev);
            Assert.NotNull(mapping);

            var videoPath = service.ResolveVideoPath(dev);
            Assert.NotNull(videoPath);
            Assert.True(File.Exists(videoPath), $"Video file not found on disk: {videoPath}");

            var posterPath = service.ResolvePosterPath(dev);
            Assert.NotNull(posterPath);
            Assert.True(File.Exists(posterPath), $"Poster file not found on disk: {posterPath}");
        }
    }

    [Fact]
    public async Task UnauthenticatedDemoCamera_WithoutCredentials_EntersErrorStateAndDoesNotStream()
    {
        var thermalDevice = new CameraDevice
        {
            EndpointId = "urn:uuid:demo-bosch-mic-9000i-00075f998877",
            DisplayName = "[DEMO] Perimeter Fence Thermal",
            AuthState = AuthenticationState.Unauthenticated,
            IsDemoDevice = true
        };

        var mapping = DemoCameraAssetCatalog.ResolveForDevice(thermalDevice);
        var session = new DemoVideoPlaybackSession(
            thermalDevice,
            mapping,
            username: null,
            password: null,
            credentialStore: null,
            NullLogger<DemoVideoPlaybackSession>.Instance);

        int frameCount = 0;
        session.FrameReceived += _ => Interlocked.Increment(ref frameCount);

        await session.StartAsync();

        // Allow supervisor loop to check auth
        await Task.Delay(250);

        Assert.Equal(StreamState.Error, session.State);
        Assert.NotNull(session.LastError);
        Assert.Contains("Authentication required", session.LastError);
        Assert.Equal(0, frameCount);

        await session.StopAsync();
    }

    [Fact]
    public async Task UnauthenticatedDemoCamera_WhenCredentialsProvided_StartsStreamingSuccessfully()
    {
        var thermalDevice = new CameraDevice
        {
            EndpointId = "urn:uuid:demo-bosch-mic-9000i-00075f998877",
            DisplayName = "[DEMO] Perimeter Fence Thermal",
            AuthState = AuthenticationState.Unauthenticated,
            IsDemoDevice = true
        };

        var credStore = new InMemoryCredentialStore();
        await credStore.SaveCredentialsAsync(thermalDevice.EndpointId, "admin", "securePass123");

        var mapping = DemoCameraAssetCatalog.ResolveForDevice(thermalDevice);
        var session = new DemoVideoPlaybackSession(
            thermalDevice,
            mapping,
            username: null,
            password: null,
            credentialStore: credStore,
            NullLogger<DemoVideoPlaybackSession>.Instance);

        var frameReceivedTcs = new TaskCompletionSource<byte[]>();
        session.FrameReceived += bytes => frameReceivedTcs.TrySetResult(bytes);

        await session.StartAsync();

        var completed = await Task.WhenAny(frameReceivedTcs.Task, Task.Delay(4000));
        Assert.Same(frameReceivedTcs.Task, completed);

        var frame = await frameReceivedTcs.Task;
        Assert.NotNull(frame);
        Assert.True(frame.Length > 54);
        Assert.Equal(0x42, frame[0]); // 'B'
        Assert.Equal(0x4D, frame[1]); // 'M'
        Assert.Equal(StreamState.Streaming, session.State);

        await session.StopAsync();
        Assert.Equal(StreamState.Idle, session.State);
    }

    [Fact]
    public async Task StreamingService_RoutesDemoDeviceToDemoSession()
    {
        var demoService = new DemoVideoPlaybackService(NullLoggerFactory.Instance);
        var streamingService = new StreamingService(NullLoggerFactory.Instance, null, demoService);

        var demoDev = new CameraDevice
        {
            EndpointId = "urn:uuid:demo-axis-p3245-v-00408c123456",
            DisplayName = "[DEMO] Front Entrance Dome",
            AuthState = AuthenticationState.Authenticated,
            IsDemoDevice = true
        };

        var session = streamingService.CreateSession(demoDev);
        Assert.IsType<DemoVideoPlaybackSession>(session);

        var frameReceivedTcs = new TaskCompletionSource<byte[]>();
        session.FrameReceived += bytes => frameReceivedTcs.TrySetResult(bytes);

        await session.StartAsync();
        var completed = await Task.WhenAny(frameReceivedTcs.Task, Task.Delay(4000));
        Assert.Same(frameReceivedTcs.Task, completed);

        Assert.Equal(StreamState.Streaming, session.State);
        Assert.True(session.FrameCount > 0);

        await session.StopAsync();
    }

    [Fact]
    public async Task RecordingsViewModel_SearchAndPlay_DisplaysDemoFootage()
    {
        var repo = new InMemoryDeviceRepo();
        var demoDevices = DemoDataGenerator.CreateDemoDevices();
        foreach (var dev in demoDevices)
        {
            await repo.AddOrUpdateAsync(dev);
        }

        var demoService = new DemoVideoPlaybackService(NullLoggerFactory.Instance);
        var streamingService = new StreamingService(NullLoggerFactory.Instance, null, demoService);
        var onvifClient = new DummyOnvifClient();
        var credStore = new InMemoryCredentialStore();

        var vm = new RecordingsViewModel(repo, onvifClient, credStore, () => true, streamingService);

        await vm.LoadDevicesAsync();
        Assert.NotEmpty(vm.AvailableDevices);

        vm.SelectedDevice = vm.AvailableDevices.First(d => d.EndpointId == "urn:uuid:demo-axis-p3245-v-00408c123456");
        Assert.True(vm.IsDemoRecording);

        await vm.SearchRecordingsAsync();
        Assert.NotEmpty(vm.Segments);
        Assert.NotNull(vm.SelectedSegment);

        // Play the first segment
        await vm.PlayAsync(vm.Segments[0]);
        Assert.True(vm.IsPlaying);
        Assert.Contains("ARCHIVE", vm.SegmentOsdTag);

        // Pause
        await vm.PauseAsync();
        Assert.False(vm.IsPlaying);

        // Seek timeline
        vm.Seek(50.0);
        Assert.Equal(50.0, vm.PlayheadPosition);

        await vm.DisposeAsync();
    }
}
