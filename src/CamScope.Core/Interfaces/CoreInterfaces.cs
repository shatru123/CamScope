using CamScope.Core.Enums;
using CamScope.Core.Models;

namespace CamScope.Core.Interfaces;

public interface INetworkDiscoveryEngine
{
    DiscoveryStatus Status { get; }
    Task<IReadOnlyList<CameraDevice>> DiscoverAsync(
        NetworkInterfaceInfo iface,
        CancellationToken ct = default,
        IProgress<DiscoveryProgressReport>? progress = null);
    void Cancel();
}

public interface ICredentialStore
{
    Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default);
    Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default);
    Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default);
}

public interface IDeviceRepository
{
    Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default);
    Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default);
    Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}

public interface IAuditService
{
    Task LogAsync(AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = null, CancellationToken ct = default);
    Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int count = 100, CancellationToken ct = default);
    Task ClearLogsAsync(CancellationToken ct = default);
}

public interface INetworkInterfaceProvider
{
    IReadOnlyList<NetworkInterfaceInfo> GetEligibleInterfaces(bool includeLoopback = false);
}

public interface IStreamingSession : IAsyncDisposable
{
    Guid SessionId { get; }
    CameraDevice Device { get; }
    StreamState State { get; }
    int FrameCount { get; }
    double CurrentFps { get; }
    string? StreamResolution { get; }
    string? LastError { get; }

    event Action<byte[]>? FrameReceived;
    event Action<StreamState, string?>? StateChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public interface IStreamingService
{
    IStreamingSession CreateSession(CameraDevice device, string? username = null, string? password = null);
}

public record DeviceInformationResult(
    string Manufacturer,
    string Model,
    string FirmwareVersion,
    string SerialNumber,
    string HardwareId);

public interface IOnvifClient
{
    Task<bool> ProbeReachableAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default);
    Task<DeviceInformationResult> GetDeviceInformationAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default);
    Task<DeviceCapabilities> GetCapabilitiesAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default);
    Task<string?> GetStreamUriAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default);
    Task<IReadOnlyList<RecordingSegment>> FindRecordingsAsync(string serviceAddress, DateTime startUtc, DateTime endUtc, string? username, string? password, CancellationToken ct = default);
}

public interface ISettingsService
{
    Task<ApplicationSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken ct = default);
}
