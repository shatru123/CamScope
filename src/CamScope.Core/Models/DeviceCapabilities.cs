using CamScope.Core.Enums;

namespace CamScope.Core.Models;

public sealed class DeviceCapabilities
{
    public CapabilityStatus LiveStreaming { get; set; } = CapabilityStatus.Unknown;
    public CapabilityStatus Ptz { get; set; } = CapabilityStatus.Unknown;
    public CapabilityStatus RecordingSearch { get; set; } = CapabilityStatus.Unknown;
    public CapabilityStatus Audio { get; set; } = CapabilityStatus.Unknown;
    public CapabilityStatus Analytics { get; set; } = CapabilityStatus.Unknown;

    public static DeviceCapabilities Default() => new();

    public static DeviceCapabilities SupportedAll() => new()
    {
        LiveStreaming = CapabilityStatus.Supported,
        Ptz = CapabilityStatus.Supported,
        RecordingSearch = CapabilityStatus.Supported,
        Audio = CapabilityStatus.Supported,
        Analytics = CapabilityStatus.Supported
    };

    public static DeviceCapabilities StreamOnly() => new()
    {
        LiveStreaming = CapabilityStatus.Supported,
        Ptz = CapabilityStatus.Unsupported,
        RecordingSearch = CapabilityStatus.Unsupported,
        Audio = CapabilityStatus.Unsupported,
        Analytics = CapabilityStatus.Unsupported
    };
}
