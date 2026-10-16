using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    private readonly TimeSpan _processTimeout;

    public NetworkInterfaceProvider(
        ILogger<NetworkInterfaceProvider> logger,
        TimeSpan? processTimeout = null)
    {
        _logger = logger;
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(2);
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetEligibleInterfaces(bool includeLoopback = false)
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

                var ipStr = ipv4Info.Address.ToString();
                var isLoopback = iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                                 ipStr.StartsWith("127.", StringComparison.Ordinal);

                if (!includeLoopback && isLoopback)
                    continue;

                var supportsMulticast = iface.SupportsMulticast;

                result.Add(new NetworkInterfaceInfo
                {
                    Id = iface.Id,
                    Name = iface.Name,
                    Description = isLoopback ? $"{iface.Description} (Diagnostic / Loopback)" : iface.Description,
                    IPv4Address = ipStr,
                    SubnetMask = ipv4Info.IPv4Mask?.ToString() ?? "255.255.255.0",
                    IsUp = true,
                    IsLoopback = isLoopback,
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
            TryEnumerateViaIfconfig(result, includeLoopback);
        }

        if (result.Count == 0 && includeLoopback)
        {
            result.Add(new NetworkInterfaceInfo
            {
                Id = "lo0",
                Name = "lo0",
                Description = "Default Local Loopback Adapter (Diagnostic)",
                IPv4Address = "127.0.0.1",
                SubnetMask = "255.0.0.0",
                IsUp = true,
                IsLoopback = true,
                SupportsMulticast = true
            });
        }

        // Sort: physical interfaces first, loopback last
        return result
            .OrderBy(i => i.IsLoopback)
            .ThenBy(i => i.Name)
            .ToList();
    }

    private void TryEnumerateViaIfconfig(List<NetworkInterfaceInfo> list, bool includeLoopback)
    {
        var output = RunIfconfigWithTimeout(_processTimeout);
        if (string.IsNullOrWhiteSpace(output)) return;

        var parsed = ParseIfconfigOutput(output, includeLoopback);
        list.AddRange(parsed);
    }

    public string? RunIfconfigWithTimeout(TimeSpan timeout)
    {
        try
        {
            var ifconfigPath = File.Exists("/sbin/ifconfig") ? "/sbin/ifconfig" : "ifconfig";
            var psi = new ProcessStartInfo
            {
                FileName = ifconfigPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                _logger.LogWarning("Failed to launch ifconfig process");
                return null;
            }

            using var cts = new CancellationTokenSource(timeout);
            var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);

            if (readTask.Wait(timeout))
            {
                process.WaitForExit(500);
                return readTask.Result;
            }
            else
            {
                _logger.LogWarning("ifconfig process timed out after {TimeoutMs}ms; killing process", timeout.TotalMilliseconds);
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore process kill failure if already terminating
                }
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ifconfig execution cancelled due to timeout");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ifconfig fallback enumeration unavailable");
            return null;
        }
    }

    public static List<NetworkInterfaceInfo> ParseIfconfigOutput(string output, bool includeLoopback = false)
    {
        var result = new List<NetworkInterfaceInfo>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Split('\n');
        string currentIface = "";
        bool currentUp = false;
        bool currentMulticast = false;
        bool currentLoopback = false;

        var ifaceHeaderRegex = new Regex(@"^([a-z0-9_]+):\s*flags=\d+<([^>]+)>", RegexOptions.IgnoreCase);
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
                currentMulticast = flags.Contains("MULTICAST", StringComparison.OrdinalIgnoreCase);
                currentLoopback = flags.Contains("LOOPBACK", StringComparison.OrdinalIgnoreCase) ||
                                  currentIface.StartsWith("lo", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (currentUp && !string.IsNullOrEmpty(currentIface))
            {
                var inetMatch = inetRegex.Match(line);
                if (inetMatch.Success)
                {
                    var rawIp = inetMatch.Groups[1].Value;
                    var rawMask = inetMatch.Groups[2].Value;

                    // Validate IPv4
                    if (!IPAddress.TryParse(rawIp, out var parsedIp) ||
                        parsedIp.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var isLoopback = currentLoopback || rawIp.StartsWith("127.", StringComparison.Ordinal);
                    if (!includeLoopback && isLoopback)
                    {
                        continue;
                    }

                    var normalizedMask = NormalizeNetmask(rawMask);

                    result.Add(new NetworkInterfaceInfo
                    {
                        Id = currentIface,
                        Name = currentIface,
                        Description = isLoopback
                            ? "Loopback Adapter (Diagnostic)"
                            : (currentIface.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                                ? "Ethernet / Wi-Fi Adapter"
                                : "Network Adapter"),
                        IPv4Address = rawIp,
                        SubnetMask = normalizedMask,
                        IsUp = true,
                        IsLoopback = isLoopback,
                        SupportsMulticast = currentMulticast
                    });
                }
            }
        }

        return result;
    }

    public static string NormalizeNetmask(string rawMask)
    {
        if (string.IsNullOrWhiteSpace(rawMask))
            return "255.255.255.0";

        if (rawMask.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && rawMask.Length >= 10)
        {
            if (uint.TryParse(rawMask.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                var b1 = (hex >> 24) & 0xFF;
                var b2 = (hex >> 16) & 0xFF;
                var b3 = (hex >> 8) & 0xFF;
                var b4 = hex & 0xFF;
                return $"{b1}.{b2}.{b3}.{b4}";
            }
        }

        if (IPAddress.TryParse(rawMask, out var parsedMask) && parsedMask.AddressFamily == AddressFamily.InterNetwork)
        {
            return rawMask;
        }

        return "255.255.255.0";
    }
}
