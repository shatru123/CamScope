using System.Net.NetworkInformation;
using System.Net.Sockets;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkInterfaceProvider : INetworkInterfaceProvider
{
    private readonly ILogger<NetworkInterfaceProvider> _logger;

    public NetworkInterfaceProvider(ILogger<NetworkInterfaceProvider> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetEligibleInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            var allInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in allInterfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipProps = iface.GetIPProperties();
                var ipv4Info = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4Info == null)
                    continue;

                var isLoopback = iface.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                var supportsMulticast = iface.SupportsMulticast;

                result.Add(new NetworkInterfaceInfo
                {
                    Id = iface.Id,
                    Name = iface.Name,
                    Description = isLoopback ? $"{iface.Description} (Loopback/Test)" : iface.Description,
                    IPv4Address = ipv4Info.Address.ToString(),
                    SubnetMask = ipv4Info.IPv4Mask?.ToString() ?? "255.255.255.0",
                    IsUp = true,
                    SupportsMulticast = supportsMulticast,
                    SpeedBytesPerSec = iface.Speed
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate network interfaces");
        }

        if (result.Count == 0)
        {
            result.Add(new NetworkInterfaceInfo
            {
                Id = "lo0",
                Name = "lo0",
                Description = "Default Local Adapter",
                IPv4Address = "127.0.0.1",
                SubnetMask = "255.0.0.0",
                IsUp = true,
                SupportsMulticast = true
            });
        }

        // Sort: physical/wifi interfaces first, loopback last
        return result
            .OrderBy(i => i.IPv4Address.StartsWith("127."))
            .ThenBy(i => i.Name)
            .ToList();
    }
}
