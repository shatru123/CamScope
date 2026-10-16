namespace CamScope.Core.Enums;

public enum DeviceConnectionState
{
    Discovered,
    Reachable,
    Authenticating,
    Authenticated,
    Connected,
    Unavailable,
    Error
}

public enum AuthenticationState
{
    Unauthenticated,
    Authenticating,
    Authenticated,
    BadCredentials,
    LockedOut,
    Failed
}

public enum DiscoveryStatus
{
    Idle,
    BindingInterface,
    SendingProbes,
    AwaitingResponses,
    ProcessingDevices,
    Completed,
    Cancelled,
    Failed
}

public enum CameraProtocolType
{
    Onvif,
    RTSP,
    VendorSpecific,
    Simulated
}

public enum CapabilityStatus
{
    Unknown,
    Supported,
    Unsupported
}

public enum StreamState
{
    Idle,
    Connecting,
    Streaming,
    Reconnecting,
    Paused,
    Error,
    Stopped
}

public enum AuditAction
{
    DiscoveryStarted,
    DiscoveryCompleted,
    DiscoveryCancelled,
    DeviceDiscovered,
    DeviceAddedManually,
    DeviceAuthenticated,
    AuthenticationFailed,
    StreamStarted,
    StreamStopped,
    DeviceRemoved,
    SettingsChanged,
    InventoryExported,
    InventoryCleared,
    NetworkInterfaceSelected,
    SubnetScanStarted,
    SubnetScanCompleted
}

public enum ThemeMode
{
    Dark,
    Light
}

public enum NetworkInterfaceKind
{
    Ethernet,
    WiFi,
    UsbEthernet,
    Thunderbolt,
    Vpn,
    Loopback,
    Virtual,
    Other
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}
