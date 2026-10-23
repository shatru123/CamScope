using CamScope.Core.Enums;
using CamScope.Core.Models;

namespace CamScope.Application.Services;

public static class DemoDataGenerator
{
    public static IReadOnlyList<CameraDevice> CreateDemoDevices()
    {
        return
        [
            new CameraDevice
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                EndpointId = "urn:uuid:demo-axis-p3245-v-00408c123456",
                DisplayName = "[DEMO] Front Entrance Dome",
                Vendor = "Axis Communications",
                Model = "P3245-V",
                FirmwareVersion = "10.12.3",
                SerialNumber = "ACCC8E123456",
                Host = "192.168.1.101",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.101/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.101/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/Front_Entrance",
                    "onvif://www.onvif.org/hardware/P3245-V",
                    "onvif://www.onvif.org/location/Building_A_Main_Gate",
                    "onvif://www.onvif.org/Profile/Streaming"
                ],
                ConnectionState = DeviceConnectionState.Connected,
                AuthState = AuthenticationState.Authenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Supported,
                    Ptz = CapabilityStatus.Unsupported,
                    RecordingSearch = CapabilityStatus.Supported,
                    Audio = CapabilityStatus.Supported,
                    Analytics = CapabilityStatus.Supported
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "Building A - Main Entrance",
                Tags = ["Exterior", "HighPriority", "Entrance"],
                RtspStreamUri = "rtsp://192.168.1.101:554/live/ch0"
            },
            new CameraDevice
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                EndpointId = "urn:uuid:demo-hanwha-xnp6400rw-00166c654321",
                DisplayName = "[DEMO] Loading Dock PTZ",
                Vendor = "Hanwha Techwin",
                Model = "XNP-6400RW",
                FirmwareVersion = "2.14.01",
                SerialNumber = "HTW400987654",
                Host = "192.168.1.102",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.102/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.102/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/Loading_Dock_PTZ",
                    "onvif://www.onvif.org/hardware/XNP-6400RW",
                    "onvif://www.onvif.org/location/Warehouse_Dock_4",
                    "onvif://www.onvif.org/type/NetworkVideoTransmitter"
                ],
                ConnectionState = DeviceConnectionState.Connected,
                AuthState = AuthenticationState.Authenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Supported,
                    Ptz = CapabilityStatus.Supported,
                    RecordingSearch = CapabilityStatus.Unsupported,
                    Audio = CapabilityStatus.Unsupported,
                    Analytics = CapabilityStatus.Supported
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "Warehouse 2 - Loading Bay 4",
                Tags = ["Warehouse", "PTZ", "Logistics"],
                RtspStreamUri = "rtsp://192.168.1.102:554/profile1/media.smp"
            },
            new CameraDevice
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                EndpointId = "urn:uuid:demo-hikvision-ds2cd2143g2-40b4cd112233",
                DisplayName = "[DEMO] Server Room Fixed",
                Vendor = "Hikvision",
                Model = "DS-2CD2143G2-I",
                FirmwareVersion = "V5.7.13",
                SerialNumber = "DS-2CD2143G2-I20230101AAWR",
                Host = "192.168.1.104",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.104/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.104/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/Server_Room_East",
                    "onvif://www.onvif.org/hardware/DS-2CD2143G2-I",
                    "onvif://www.onvif.org/location/Datacenter_Room_102"
                ],
                ConnectionState = DeviceConnectionState.Connected,
                AuthState = AuthenticationState.Authenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Supported,
                    Ptz = CapabilityStatus.Unsupported,
                    RecordingSearch = CapabilityStatus.Supported,
                    Audio = CapabilityStatus.Unsupported,
                    Analytics = CapabilityStatus.Supported
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "Datacenter - Server Room East",
                Tags = ["Security", "Datacenter", "Restricted"],
                RtspStreamUri = "rtsp://192.168.1.104:554/Streaming/Channels/101"
            },
            new CameraDevice
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                EndpointId = "urn:uuid:demo-bosch-mic-9000i-00075f998877",
                DisplayName = "[DEMO] Perimeter Fence Thermal",
                Vendor = "Bosch Security Systems",
                Model = "MIC IP fusion 9000i",
                FirmwareVersion = "7.82.0039",
                SerialNumber = "BOSCH9000I-887766",
                Host = "192.168.1.103",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.103/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.103/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/Perimeter_Thermal_North",
                    "onvif://www.onvif.org/hardware/MIC_IP_fusion_9000i",
                    "onvif://www.onvif.org/location/North_Perimeter"
                ],
                ConnectionState = DeviceConnectionState.Reachable,
                AuthState = AuthenticationState.Unauthenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Unknown,
                    Ptz = CapabilityStatus.Unknown,
                    RecordingSearch = CapabilityStatus.Unknown
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "North Perimeter Fence",
                Tags = ["Thermal", "Perimeter", "Unauthenticated"]
            },
            new CameraDevice
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                EndpointId = "urn:uuid:demo-dahua-ipc-hfw5842e-3c52a1334455",
                DisplayName = "[DEMO] Parking Lot 4K Bullet",
                Vendor = "Dahua Technology",
                Model = "IPC-HFW5842E-Z4E",
                FirmwareVersion = "V2.840.0000000.12.R",
                SerialNumber = "DH842EZ4E776655",
                Host = "192.168.1.105",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.105/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.105/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/South_Parking_Lot",
                    "onvif://www.onvif.org/hardware/IPC-HFW5842E-Z4E"
                ],
                ConnectionState = DeviceConnectionState.Connected,
                AuthState = AuthenticationState.Authenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Supported,
                    Ptz = CapabilityStatus.Unsupported,
                    RecordingSearch = CapabilityStatus.Supported,
                    Audio = CapabilityStatus.Supported,
                    Analytics = CapabilityStatus.Supported
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "South Employee Parking",
                Tags = ["Exterior", "Parking", "4K"],
                RtspStreamUri = "rtsp://192.168.1.105:554/cam/realmonitor?channel=1&subtype=0"
            },
            new CameraDevice
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                EndpointId = "urn:uuid:demo-sony-snc-vb770-001a2b3c4d5e",
                DisplayName = "[DEMO] Corporate Reception Lobby",
                Vendor = "Sony Security",
                Model = "SNC-VB770",
                FirmwareVersion = "1.8.0",
                SerialNumber = "SONY770VB991122",
                Host = "192.168.1.106",
                Port = 80,
                PrimaryServiceAddress = "http://192.168.1.106/onvif/device_service",
                ServiceAddresses = ["http://192.168.1.106/onvif/device_service"],
                Scopes =
                [
                    "onvif://www.onvif.org/name/Corporate_Reception",
                    "onvif://www.onvif.org/hardware/SNC-VB770",
                    "onvif://www.onvif.org/location/Main_Lobby"
                ],
                ConnectionState = DeviceConnectionState.Connected,
                AuthState = AuthenticationState.Authenticated,
                Capabilities = new DeviceCapabilities
                {
                    LiveStreaming = CapabilityStatus.Supported,
                    Ptz = CapabilityStatus.Unsupported,
                    RecordingSearch = CapabilityStatus.Supported,
                    Audio = CapabilityStatus.Supported,
                    Analytics = CapabilityStatus.Supported
                },
                Protocol = CameraProtocolType.Simulated,
                IsDemoDevice = true,
                Location = "Headquarters - Reception Lobby",
                Tags = ["Interior", "Lobby", "Reception"],
                RtspStreamUri = "rtsp://192.168.1.106:554/live/media"
            }
        ];
    }

    public static IReadOnlyList<RecordingSegment> CreateDemoRecordings(string deviceId)
    {
        var now = DateTime.UtcNow;
        var startOfToday = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        return
        [
            new RecordingSegment
            {
                Id = "seg-001",
                DeviceId = deviceId,
                StartTimeUtc = startOfToday.AddHours(2),
                EndTimeUtc = startOfToday.AddHours(4),
                TrackName = "Primary Continuous Track"
            },
            new RecordingSegment
            {
                Id = "seg-002",
                DeviceId = deviceId,
                StartTimeUtc = startOfToday.AddHours(7).AddMinutes(15),
                EndTimeUtc = startOfToday.AddHours(9).AddMinutes(45),
                TrackName = "Motion Triggered Track"
            },
            new RecordingSegment
            {
                Id = "seg-003",
                DeviceId = deviceId,
                StartTimeUtc = startOfToday.AddHours(11).AddMinutes(30),
                EndTimeUtc = startOfToday.AddHours(14),
                TrackName = "Continuous Schedule"
            },
            new RecordingSegment
            {
                Id = "seg-004",
                DeviceId = deviceId,
                StartTimeUtc = startOfToday.AddHours(16),
                EndTimeUtc = startOfToday.AddHours(19).AddMinutes(20),
                TrackName = "Event Alarm Track"
            }
        ];
    }
}
