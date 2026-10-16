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
    SubnetScanCompleted,
    WifiSecurityScanStarted,
    WifiSecurityScanCompleted,
    WifiSecurityAssessmentRun,
    WifiAuditRecordSaved,
    WifiAuditRecordDeleted
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

public enum FindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Informational
}

public enum FindingConfidence
{
    Confirmed,
    Inferred,
    ObservableAdvertisementOnly,
    UnobservableRouterSideRequired
}

public enum PmfStatus
{
    Mandatory,
    Capable,
    Disabled,
    UnknownNotObservable
}

public enum SecurityPostureLevel
{
    Secure,
    Satisfactory,
    NeedsAttention,
    Vulnerable,
    CriticalRisk
}

public enum WifiSecurityStandard
{
    Open,
    Wep,
    WpaPersonal,
    WpaEnterprise,
    Wpa2Personal,
    Wpa2Enterprise,
    Wpa3Personal,
    Wpa3Enterprise,
    Wpa2Wpa3Transition,
    Unknown
}
