using CamScope.Core.Enums;

namespace CamScope.Core.Models;

public sealed class CameraDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EndpointId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Vendor { get; set; } = "Unknown";
    public string Model { get; set; } = "Unknown";
    public string FirmwareVersion { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string? PrimaryServiceAddress { get; set; }
    public List<string> ServiceAddresses { get; set; } = [];
    public List<string> Scopes { get; set; } = [];
    public DeviceConnectionState ConnectionState { get; set; } = DeviceConnectionState.Discovered;
    public AuthenticationState AuthState { get; set; } = AuthenticationState.Unauthenticated;
    public DeviceCapabilities Capabilities { get; set; } = new();
    public CameraProtocolType Protocol { get; set; } = CameraProtocolType.Onvif;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
    public bool IsDemoDevice { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Location { get; set; }
    public string? RtspStreamUri { get; set; }

    public bool IsAuthenticated => AuthState == AuthenticationState.Authenticated;
    public bool CanStream => Capabilities.LiveStreaming == CapabilityStatus.Supported;
    public bool CanSearchRecordings => Capabilities.RecordingSearch == CapabilityStatus.Supported;

    public override string ToString() =>
        $"{DisplayName} ({Vendor} {Model}) @ {Host}:{Port} [{ConnectionState}/{AuthState}]";
}
