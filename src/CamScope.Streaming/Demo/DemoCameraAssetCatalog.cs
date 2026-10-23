using System.Collections.Frozen;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;

namespace CamScope.Streaming.Demo;

/// <summary>
/// Provides explicit, deterministic mapping between Demo Camera devices and their bundled CCTV video assets.
/// Avoids fragile substring matching by keying primarily on CameraDevice.EndpointId and Device.Id.
/// </summary>
public static class DemoCameraAssetCatalog
{
    private static readonly FrozenDictionary<string, DemoCameraAssetMapping> CatalogByEndpoint;
    private static readonly DemoCameraAssetMapping DefaultMapping;

    static DemoCameraAssetCatalog()
    {
        var mappings = new List<DemoCameraAssetMapping>
        {
            new(
                EndpointId: "urn:uuid:demo-axis-p3245-v-00408c123456",
                DisplayName: "[DEMO] Front Entrance Dome",
                Location: "Building A - Main Entrance",
                VideoFileName: "cctv_front_entrance.mp4",
                PosterFileName: "cctv_front_entrance.jpg",
                TargetFps: 25,
                Resolution: "1280x720"),

            new(
                EndpointId: "urn:uuid:demo-hanwha-xnp6400rw-00166c654321",
                DisplayName: "[DEMO] Loading Dock PTZ",
                Location: "Warehouse 2 - Loading Bay 4",
                VideoFileName: "cctv_loading_dock.mp4",
                PosterFileName: "cctv_loading_dock.jpg",
                TargetFps: 25,
                Resolution: "1280x720"),

            new(
                EndpointId: "urn:uuid:demo-hikvision-ds2cd2143g2-40b4cd112233",
                DisplayName: "[DEMO] Server Room Fixed",
                Location: "Datacenter - Server Room East",
                VideoFileName: "cctv_server_room.mp4",
                PosterFileName: "cctv_server_room.jpg",
                TargetFps: 25,
                Resolution: "1280x720"),

            new(
                EndpointId: "urn:uuid:demo-bosch-mic-9000i-00075f998877",
                DisplayName: "[DEMO] Perimeter Fence Thermal",
                Location: "North Perimeter Fence",
                VideoFileName: "cctv_perimeter_fence_thermal.mp4",
                PosterFileName: "cctv_perimeter_fence_thermal.jpg",
                TargetFps: 25,
                Resolution: "1280x720"),

            new(
                EndpointId: "urn:uuid:demo-dahua-ipc-hfw5842e-3c52a1334455",
                DisplayName: "[DEMO] Parking Lot 4K Bullet",
                Location: "South Employee Parking",
                VideoFileName: "cctv_parking_lot.mp4",
                PosterFileName: "cctv_parking_lot.jpg",
                TargetFps: 25,
                Resolution: "1280x720"),

            new(
                EndpointId: "urn:uuid:demo-sony-snc-vb770-001a2b3c4d5e",
                DisplayName: "[DEMO] Corporate Reception Lobby",
                Location: "Headquarters - Reception Lobby",
                VideoFileName: "cctv_reception_lobby.mp4",
                PosterFileName: "cctv_reception_lobby.jpg",
                TargetFps: 25,
                Resolution: "1280x720")
        };

        CatalogByEndpoint = mappings.ToFrozenDictionary(m => m.EndpointId, StringComparer.OrdinalIgnoreCase);
        DefaultMapping = mappings[0];
    }

    public static IReadOnlyCollection<DemoCameraAssetMapping> AllMappings => CatalogByEndpoint.Values;

    public static DemoCameraAssetMapping? FindByEndpointId(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return null;
        return CatalogByEndpoint.GetValueOrDefault(endpointId);
    }

    public static DemoCameraAssetMapping ResolveForDevice(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (FindByEndpointId(device.EndpointId) is { } matchedByEndpoint)
        {
            return matchedByEndpoint;
        }

        // Secondary check by display name keywords if custom demo endpoint ID
        var name = device.DisplayName.ToLowerInvariant();
        foreach (var mapping in CatalogByEndpoint.Values)
        {
            if (name.Contains(mapping.VideoFileName.Replace(".mp4", "").Replace("cctv_", ""), StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        if (name.Contains("dock") || name.Contains("warehouse")) return CatalogByEndpoint["urn:uuid:demo-hanwha-xnp6400rw-00166c654321"];
        if (name.Contains("server") || name.Contains("datacenter")) return CatalogByEndpoint["urn:uuid:demo-hikvision-ds2cd2143g2-40b4cd112233"];
        if (name.Contains("thermal") || name.Contains("fence") || name.Contains("perimeter")) return CatalogByEndpoint["urn:uuid:demo-bosch-mic-9000i-00075f998877"];
        if (name.Contains("parking")) return CatalogByEndpoint["urn:uuid:demo-dahua-ipc-hfw5842e-3c52a1334455"];
        if (name.Contains("reception") || name.Contains("lobby")) return CatalogByEndpoint["urn:uuid:demo-sony-snc-vb770-001a2b3c4d5e"];

        return DefaultMapping;
    }
}
