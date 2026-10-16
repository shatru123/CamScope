using CamScope.Core.Enums;

namespace CamScope.Core.Models;

public sealed class NetworkInterfaceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IPv4Address { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public bool SupportsMulticast { get; set; }
    public bool IsLoopback { get; set; }
    public long SpeedBytesPerSec { get; set; }

    public string DisplayText => $"{Name} - {IPv4Address} ({Description})";
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
