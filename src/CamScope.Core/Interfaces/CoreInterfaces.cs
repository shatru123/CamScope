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

public interface INetworkInventoryService
{
    Task<NetworkInventoryReport> GetNetworkInventoryAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task<NetworkRoutingSnapshot> CaptureRoutingSnapshotAsync(CancellationToken ct = default);
    RoutingInvariantResult VerifyRoutingInvariant(NetworkRoutingSnapshot? before, NetworkRoutingSnapshot? after);
    bool VerifyRoutingInvariant(NetworkRoutingSnapshot before, NetworkRoutingSnapshot after, out string? mutationWarning);
}

public interface INetworkSelectionService
{
    NetworkInterfaceInfo? CurrentScanningInterface { get; }
    NetworkInterfaceInfo? CctvInterface { get; }
    WifiConnectionInfo? CurrentCctvWifiConnection { get; }
    void SelectScanningInterface(NetworkInterfaceInfo iface);
    void SetCctvInterface(NetworkInterfaceInfo? iface, WifiConnectionInfo? connection = null);
    event Action<NetworkInterfaceInfo?>? SelectionChanged;
    event Action<NetworkInterfaceInfo?>? CctvInterfaceChanged;
}

public interface IWifiNetworkManager
{
    bool IsPlatformSupported { get; }
    bool CanSupportConcurrentConnections { get; }
    Task<SecondaryAdapterCapability> CheckSecondaryCapabilityAsync(string? targetInterfaceName, CancellationToken ct = default);
    Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default);
    Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default);
    Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default);
    Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default);
    Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default);
}

public interface INetworkPermissionService
{
    Task<bool> HasWifiScanningPermissionAsync();
    string GetPermissionExplanation();
}

public interface ISubnetScanner
{
    Task<IReadOnlyList<CameraDevice>> ScanSubnetAsync(
        SubnetScanRequest request,
        IProgress<DiscoveryProgressReport>? progress = null,
        CancellationToken ct = default);
}

public interface IWifiSecurityScanner
{
    bool IsPlatformSupported { get; }
    Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default);
}

public interface IWifiSecurityAnalyzer
{
    SecurityAssessmentReport Analyze(IReadOnlyList<WifiAccessPointObservation> observations);
}

public interface IPasswordStrengthEstimator
{
    PasswordStrengthEstimate Estimate(string password);
}

public interface IWifiSecurityRepository
{
    Task SaveScanSessionAsync(WifiScanSession session, CancellationToken ct = default);
    Task<IReadOnlyList<WifiScanSession>> GetScanHistoryAsync(int limit = 50, CancellationToken ct = default);
    Task DeleteScanSessionAsync(string id, CancellationToken ct = default);
    Task ClearScanHistoryAsync(CancellationToken ct = default);
    Task SaveAuditRecordAsync(AuthorizedAuditRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<AuthorizedAuditRecord>> GetAuditRecordsAsync(string? ssid = null, CancellationToken ct = default);
    Task DeleteAuditRecordAsync(string id, CancellationToken ct = default);
}

public interface INistPasswordAuditor
{
    NistPasswordAuditResult AuditPassword(
        string password,
        bool isPrivilegedAdmin = false,
        string? contextUsername = null,
        string? contextEntityName = null);

    string GenerateCompliantPassphrase(int wordCount = 4, string separator = "-");
}

public interface IAdminAuthenticationController
{
    Task<AdminAuthAttemptResult> AuthenticateAsync(
        string username,
        string candidatePassword,
        string clientIp = "127.0.0.1",
        CancellationToken ct = default);

    Task<bool> SetPasswordAsync(string username, string newPassword, CancellationToken ct = default);
    Task ResetLockoutAsync(string username, CancellationToken ct = default);
    AdminAccountState GetAccountState(string username);
}
