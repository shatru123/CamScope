using System.Collections.Concurrent;
using CamScope.Core.Models;

namespace CamScope.Discovery.Deduplication;

public sealed class DeviceDeduplicator
{
    private readonly ConcurrentDictionary<string, CameraDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAddOrUpdate(CameraDevice incoming, out CameraDevice current)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var key = GetDeduplicationKey(incoming);

        var added = false;
        current = _devices.AddOrUpdate(
            key,
            addValueFactory: _ =>
            {
                added = true;
                return incoming;
            },
            updateValueFactory: (_, existing) =>
            {
                // Merge scopes and service addresses
                foreach (var addr in incoming.ServiceAddresses)
                {
                    if (!existing.ServiceAddresses.Contains(addr, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.ServiceAddresses.Add(addr);
                    }
                }

                foreach (var scope in incoming.Scopes)
                {
                    if (!existing.Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Scopes.Add(scope);
                    }
                }

                // Update timestamps and details if more specific
                existing.LastSeenUtc = DateTime.UtcNow;
                if (existing.Model == "ONVIF Compatible" && incoming.Model != "ONVIF Compatible")
                {
                    existing.Model = incoming.Model;
                }
                if (existing.Vendor == "Unknown" && incoming.Vendor != "Unknown")
                {
                    existing.Vendor = incoming.Vendor;
                }
                if (existing.Location == null && incoming.Location != null)
                {
                    existing.Location = incoming.Location;
                }

                return existing;
            });

        return added;
    }

    public IReadOnlyList<CameraDevice> GetAll() => _devices.Values.ToList();

    public void Clear() => _devices.Clear();

    public static string GetDeduplicationKey(CameraDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.EndpointId))
            return device.EndpointId.Trim();

        return $"{device.Host}:{device.Port}";
    }
}
