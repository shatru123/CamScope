using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
            _logger.LogDebug(ex, "System network enumeration failed; attempting fallback via ifconfig");
        }

        if (result.Count == 0)
        {
            TryEnumerateViaIfconfig(result);
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

        // Sort: physical/wifi interfaces first (non-127.x), loopback last
        return result
            .OrderBy(i => i.IPv4Address.StartsWith("127."))
            .ThenBy(i => i.Name)
            .ToList();
    }

    private void TryEnumerateViaIfconfig(List<NetworkInterfaceInfo> list)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/ifconfig",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);

            var lines = output.Split('\n');
            string currentIface = "";
            bool currentUp = false;

            var ifaceHeaderRegex = new Regex(@"^([a-z0-9]+):\s*flags=\d+<([^>]+)>", RegexOptions.IgnoreCase);
            var inetRegex = new Regex(@"inet\s+(\d+\.\d+\.\d+\.\d+)\s+netmask\s+(0x[0-9a-fA-F]+|\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();
                var headerMatch = ifaceHeaderRegex.Match(line);
                if (headerMatch.Success)
                {
                    currentIface = headerMatch.Groups[1].Value;
                    var flags = headerMatch.Groups[2].Value;
                    currentUp = flags.Contains("UP", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (currentUp && !string.IsNullOrEmpty(currentIface))
                {
                    var inetMatch = inetRegex.Match(line);
                    if (inetMatch.Success)
                    {
                        var ip = inetMatch.Groups[1].Value;
                        var mask = inetMatch.Groups[2].Value;

                        list.Add(new NetworkInterfaceInfo
                        {
                            Id = currentIface,
                            Name = currentIface,
                            Description = currentIface.StartsWith("en") ? "Ethernet / Wi-Fi Adapter" : "Network Interface",
                            IPv4Address = ip,
                            SubnetMask = mask,
                            IsUp = true,
                            SupportsMulticast = true
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ifconfig fallback enumeration unavailable");
        }
    }
}
