using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Discovery.Engine;
using CamScope.Persistence;
using CamScope.Persistence.Repositories;
using CamScope.Persistence.Services;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.IntegrationTests;

public sealed class SqlitePersistenceTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;
    private readonly DatabaseInitializer _initializer;
    private readonly SqliteDeviceRepository _repository;
    private readonly SqliteAuditService _auditService;

    public SqlitePersistenceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"camscope_test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
        _initializer = new DatabaseInitializer(_connectionString);
        _initializer.Initialize();
        _repository = new SqliteDeviceRepository(_connectionString);
        _auditService = new SqliteAuditService(_connectionString);
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public async Task DeviceRepository_AddAndRetrieve_RoundTripsCorrectly()
    {
        var device = new CameraDevice
        {
            Id = Guid.NewGuid(),
            EndpointId = "urn:uuid:test-persist-device-1",
            DisplayName = "Test Persistence Camera",
            Vendor = "Hanwha",
            Model = "XNP-6400RW",
            Host = "192.168.1.200",
            Port = 80,
            PrimaryServiceAddress = "http://192.168.1.200/onvif/device_service",
            ServiceAddresses = ["http://192.168.1.200/onvif/device_service"],
            Scopes = ["onvif://www.onvif.org/name/Test"],
            ConnectionState = DeviceConnectionState.Connected,
            AuthState = AuthenticationState.Authenticated,
            Capabilities = DeviceCapabilities.SupportedAll(),
            Tags = ["TestTag", "Floor1"],
            Location = "Test Building"
        };

        await _repository.AddOrUpdateAsync(device);

        var retrieved = await _repository.GetByIdAsync(device.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(device.DisplayName, retrieved.DisplayName);
        Assert.Equal(device.Vendor, retrieved.Vendor);
        Assert.Equal(device.Host, retrieved.Host);
        Assert.Equal(AuthenticationState.Authenticated, retrieved.AuthState);
        Assert.Equal(CapabilityStatus.Supported, retrieved.Capabilities.LiveStreaming);
        Assert.Equal(CapabilityStatus.Supported, retrieved.Capabilities.RecordingSearch);
        Assert.Equal(2, retrieved.Tags.Count);
        Assert.Equal("TestTag", retrieved.Tags[0]);
        Assert.Equal("Floor1", retrieved.Tags[1]);
    }

    [Fact]
    public async Task AuditService_LogAndRetrieve_StoresSanitizedEntry()
    {
        await _auditService.LogAsync(
            AuditAction.DeviceAuthenticated,
            "192.168.1.50",
            succeeded: true,
            "Authenticated user 'admin' successfully");

        var logs = await _auditService.GetRecentLogsAsync(10);
        Assert.NotEmpty(logs);
        Assert.Equal(AuditAction.DeviceAuthenticated, logs[0].Action);
        Assert.True(logs[0].Succeeded);
        Assert.Contains("admin", logs[0].Details);
    }
}

public sealed class DemoModeIntegrationTests
{
    [Fact]
    public async Task DemoDiscoveryEngine_DiscoverAsync_ReturnsSyntheticDevicesWithoutNetwork()
    {
        var engine = new DemoDiscoveryEngine();
        var iface = new NetworkInterfaceInfo
        {
            Id = "demo-iface-1",
            Name = "demo0",
            IPv4Address = "192.168.1.5",
            IsUp = true,
            SupportsMulticast = true
        };

        var progressReports = new List<DiscoveryProgressReport>();
        var progress = new Progress<DiscoveryProgressReport>(r => progressReports.Add(r));

        var devices = await engine.DiscoverAsync(iface, CancellationToken.None, progress);

        Assert.NotEmpty(devices);
        Assert.All(devices, d =>
        {
            Assert.True(d.IsDemoDevice);
            Assert.StartsWith("[DEMO]", d.DisplayName);
            Assert.Equal(CameraProtocolType.Simulated, d.Protocol);
        });
    }

    [Fact]
    public async Task RtspStreamSession_StartAndStop_GeneratesFramesAndDisposesCleanly()
    {
        var device = new CameraDevice
        {
            DisplayName = "Test Demo Stream Camera",
            Host = "192.168.1.101",
            AuthState = AuthenticationState.Authenticated,
            IsDemoDevice = true
        };

        var logger = NullLogger<RtspStreamSession>.Instance;
        var session = new RtspStreamSession(device, "admin", "secret", logger);

        int frameCounter = 0;
        session.FrameReceived += bytes =>
        {
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 54); // Valid BMP length
            Interlocked.Increment(ref frameCounter);
        };

        await session.StartAsync();

        // Wait brief moment for connection handshake and frame pump to fire
        await Task.Delay(600);

        await session.StopAsync();
        await session.DisposeAsync();

        Assert.True(frameCounter > 0, "At least one synthetic frame should be emitted");
        Assert.Equal(StreamState.Idle, session.State);
    }

    [Fact]
    public async Task RtspStreamSession_RealStreamingWithFFmpeg_DecodesFramesAndTracksMetrics()
    {
        var ffmpeg = RtspStreamSession.FindFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            // Skip if FFmpeg is not installed on the host
            return;
        }

        var device = new CameraDevice
        {
            DisplayName = "Real Live Feed Fixture",
            Host = "127.0.0.1",
            IsDemoDevice = false,
            AuthState = AuthenticationState.Authenticated,
            RtspStreamUri = "testsrc=size=320x240:rate=10"
        };

        var logger = NullLogger<RtspStreamSession>.Instance;
        var session = new RtspStreamSession(device, null, null, logger);

        var firstFrameTcs = new TaskCompletionSource<byte[]>();
        session.FrameReceived += bytes => firstFrameTcs.TrySetResult(bytes);

        await session.StartAsync();

        var completed = await Task.WhenAny(firstFrameTcs.Task, Task.Delay(5000));
        Assert.Same(firstFrameTcs.Task, completed);

        var frameBytes = await firstFrameTcs.Task;
        Assert.NotNull(frameBytes);
        Assert.True(frameBytes.Length > 54);
        Assert.Equal(0x42, frameBytes[0]); // 'B'
        Assert.Equal(0x4D, frameBytes[1]); // 'M'

        int width = BitConverter.ToInt32(frameBytes, 18);
        int height = Math.Abs(BitConverter.ToInt32(frameBytes, 22));
        Assert.Equal(320, width);
        Assert.Equal(240, height);
        Assert.Equal("320x240", session.StreamResolution);
        Assert.True(session.FrameCount > 0);

        await session.StopAsync();
        await session.DisposeAsync();

        Assert.Equal(StreamState.Idle, session.State);
    }
}
