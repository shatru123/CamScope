using CamScope.Core.Enums;

namespace CamScope.Core.Models;

public sealed class NetworkInterfaceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HardwarePort { get; set; } = string.Empty;
    public string IPv4Address { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
    public string? GatewayAddress { get; set; }
    public string? IPv6Address { get; set; }
    public string? MacAddress { get; set; }
    public bool IsUp { get; set; }
    public bool SupportsMulticast { get; set; }
    public bool IsLoopback { get; set; }
    public bool IsPrimaryInternet { get; set; }
    public NetworkInterfaceKind InterfaceKind { get; set; } = NetworkInterfaceKind.Other;
    public long SpeedBytesPerSec { get; set; }
    public int InterfaceIndex { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(HardwarePort) || HardwarePort == Description
        ? $"{Name} - {IPv4Address} ({Description}){(IsPrimaryInternet ? " [PRIMARY]" : "")}"
        : $"{Name} [{HardwarePort}] - {IPv4Address}{(IsPrimaryInternet ? " [PRIMARY]" : "")}";
}

public sealed class DiscoverySession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InterfaceName { get; set; } = string.Empty;
    public string InterfaceIp { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DiscoveryStatus Status { get; set; } = DiscoveryStatus.Idle;
    public int ProbesSent { get; set; }
    public int DevicesFound { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DiscoveryProgressReport
{
    public DiscoveryStatus Status { get; set; }
    public int Percentage { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public int DevicesFound { get; set; }
    public CameraDevice? LatestDevice { get; set; }
}

public sealed class RecordingSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DeviceId { get; set; } = string.Empty;
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public TimeSpan Duration => EndTimeUtc - StartTimeUtc;
    public string? TrackName { get; set; }
    public string? ReplayUri { get; set; }
}

public sealed class ActivityAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AuditAction Action { get; set; }
    public string? TargetEndpoint { get; set; }
    public string? UserOrSource { get; set; } = "LocalUser";
    public bool Succeeded { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class ApplicationSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public int DiscoveryTimeoutSeconds { get; set; } = 6;
    public int MaxParallelStreams { get; set; } = 4;
    public bool AutoReconnectStreams { get; set; } = true;
    public int StreamReconnectMaxRetries { get; set; } = 5;
    public int RetainAuditDays { get; set; } = 30;
    public bool IsDemoModeEnabled { get; set; } = false;
    public string? DefaultInterfaceId { get; set; }
}

public sealed class WifiNetworkInfo
{
    public string Ssid { get; set; } = string.Empty;
    public string? Bssid { get; set; }
    public int? Rssi { get; set; }
    public bool HasSignalStrength => Rssi.HasValue;
    public int SignalQualityPercent => Rssi.HasValue
        ? Math.Clamp((int)((Rssi.Value + 100) * 2), 0, 100)
        : 0;
    public int Channel { get; set; }
    public string? Band { get; set; } = "5 GHz";
    public string Security { get; set; } = "WPA2 Personal";
    public bool IsCurrent { get; set; }
    public string? PhyMode { get; set; } = "802.11ax";

    public string SignalText => Rssi.HasValue
        ? $"{Rssi.Value} dBm ({SignalQualityPercent}%)"
        : "Unknown (Not reported by OS)";

    public bool IsSecured => !string.IsNullOrWhiteSpace(Security) &&
                             !Security.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                             !Security.Equals("Open", StringComparison.OrdinalIgnoreCase);

    public string BandGroup => (Band ?? "").Contains("6")
        ? "6 GHz"
        : (Band ?? "").Contains("5")
            ? "5 GHz"
            : "2.4 GHz";
}

public enum WifiConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
    Unsupported
}

public sealed class WifiConnectionInfo
{
    public string Ssid { get; set; } = string.Empty;
    public string? Bssid { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public string? IPv4Address { get; set; }
    public bool IsPrimaryInternet { get; set; }
    public bool IsCctvNetwork { get; set; }
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WifiConnectRequest
{
    public string Ssid { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public bool AllowSwitchingPrimaryIfSingleAdapter { get; set; }
    public bool RememberInKeychain { get; set; }
}

public sealed class WifiConnectionResult
{
    public bool Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ConnectedInterface { get; set; }
    public string? AssignedIp { get; set; }
    public bool PreservedPrimaryConnection { get; set; }
    public RoutingInvariantResult? InvariantResult { get; set; }
}

public sealed class RoutingInvariantResult
{
    public bool IsStable { get; set; }
    public bool VerificationSucceeded { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public IReadOnlyList<string> MutationDetails { get; set; } = [];
    public NetworkRoutingSnapshot? BeforeSnapshot { get; set; }
    public NetworkRoutingSnapshot? AfterSnapshot { get; set; }
}

public sealed class InterfaceBindingStatus
{
    public bool IsStrictlyBound { get; set; }
    public string InterfaceName { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; }
    public string LocalIp { get; set; } = string.Empty;
    public string BindingMethod { get; set; } = string.Empty;
    public string? LimitationWarning { get; set; }
    public string Description => IsStrictlyBound
        ? $"Strict OS Egress Pinning Active: {BindingMethod} on {InterfaceName} (Index {InterfaceIndex})"
        : $"App-Scoped Local IP Bound ({LocalIp}). {LimitationWarning ?? "Egress follows system routing table."}";
}

public sealed class NetworkInventoryReport
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; set; } = [];
    public NetworkInterfaceInfo? PrimaryInterface { get; set; }
    public IReadOnlyList<WifiNetworkInfo> WifiNetworks { get; set; } = [];
    public string? CurrentWifiSsid { get; set; }
    public bool IsWifiPermitted { get; set; } = true;
    public string? PermissionMessage { get; set; }
    public IReadOnlyList<string> SystemDnsServers { get; set; } = [];
    public string? SystemDefaultGateway { get; set; }
}

public sealed class NetworkRoutingSnapshot
{
    public string? DefaultGateway { get; set; }
    public string? PrimaryInterfaceName { get; set; }
    public IReadOnlyList<string> DnsServers { get; set; } = [];
    public string? CurrentWifiSsid { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ReachabilityDiagnostic
{
    public string InterfaceName { get; set; } = string.Empty;
    public bool IsMulticastSupported { get; set; }
    public bool IsSubnetRoutable { get; set; }
    public bool HasDefaultGateway { get; set; }
    public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Info;
    public string SummaryExplanation { get; set; } = string.Empty;
    public IReadOnlyList<string> SuggestedActions { get; set; } = [];
}

public sealed class SubnetScanRequest
{
    public NetworkInterfaceInfo TargetInterface { get; set; } = new();
    public string CidrOrSubnet { get; set; } = string.Empty;
    public TimeSpan TimeoutPerHost { get; set; } = TimeSpan.FromMilliseconds(400);
    public int MaxConcurrency { get; set; } = 16;
    public int ProbePort { get; set; } = 3702;
}
