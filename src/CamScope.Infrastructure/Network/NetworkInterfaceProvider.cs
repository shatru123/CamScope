using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

using System.Runtime.InteropServices;
using CamScope.Core.Enums;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkInterfaceProvider : INetworkInterfaceProvider
{
    private readonly ILogger<NetworkInterfaceProvider> _logger;
    private readonly TimeSpan _processTimeout;

    public event Action? InterfacesChanged;

    public NetworkInterfaceProvider(
        ILogger<NetworkInterfaceProvider> logger,
        TimeSpan? processTimeout = null)
    {
        _logger = logger;
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(2);

        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => OnNetworkStateChanged();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => OnNetworkStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to register system NetworkChange event handlers");
        }
    }

    private void OnNetworkStateChanged()
    {
        _logger.LogInformation("Host network configuration change detected. Notifying interface listeners.");
        try
        {
            InterfacesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error invoking InterfacesChanged handler");
        }
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetAllInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        var hwPorts = new Dictionary<string, (string Port, string Mac)>(StringComparer.OrdinalIgnoreCase);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var hwOutput = RunNetworkSetupHardwarePortsWithTimeout(_processTimeout);
                if (!string.IsNullOrWhiteSpace(hwOutput))
                {
                    hwPorts = NetworkInventoryService.ParseHardwarePortsOutput(hwOutput);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query macOS networksetup hardware ports");
            }
        }

        try
        {
            var allInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in allInterfaces)
            {
                var ipProps = iface.GetIPProperties();
                var ipv4Info = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var ipv6Info = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6);

                var ipStr = ipv4Info?.Address.ToString() ?? string.Empty;
                var netmask = ipv4Info?.IPv4Mask?.ToString() ?? "255.255.255.0";
                var isLoopback = iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                                 ipStr.StartsWith("127.", StringComparison.Ordinal);

                hwPorts.TryGetValue(iface.Name, out var hwInfo);
                var portName = !string.IsNullOrEmpty(hwInfo.Port) ? hwInfo.Port : iface.Description;
                var macAddress = !string.IsNullOrEmpty(hwInfo.Mac)
                    ? hwInfo.Mac
                    : NetworkInventoryService.FormatMacAddress(iface.GetPhysicalAddress().GetAddressBytes());

                var kind = NetworkInventoryService.ClassifyInterfaceKind(
                    iface.Name, portName, iface.Description, iface.NetworkInterfaceType, isLoopback);

                string? gateway = null;
                var gwAddr = ipProps.GatewayAddresses.FirstOrDefault()?.Address;
                if (gwAddr != null && !gwAddr.Equals(IPAddress.Any) && !gwAddr.Equals(IPAddress.None))
                {
                    gateway = gwAddr.ToString();
                }

                bool isUp = iface.OperationalStatus == OperationalStatus.Up;

                string? unusableReason = null;
                if (!isUp)
                {
                    unusableReason = "Interface link is down or cable unplugged";
                }
                else if (string.IsNullOrEmpty(ipStr))
                {
                    unusableReason = "No IPv4 address assigned (DHCP pending or static IP unconfigured)";
                }
                else if (isLoopback)
                {
                    unusableReason = "Loopback diagnostic adapter only";
                }
                else if (kind == NetworkInterfaceKind.Virtual || kind == NetworkInterfaceKind.Vpn)
                {
                    unusableReason = "Virtual or VPN tunnel interface";
                }

                result.Add(new NetworkInterfaceInfo
                {
                    Id = iface.Id,
                    Name = iface.Name,
                    Description = isLoopback ? $"{iface.Description} (Diagnostic / Loopback)" : iface.Description,
                    HardwarePort = portName,
                    IPv4Address = ipStr,
                    SubnetMask = netmask,
                    GatewayAddress = gateway,
                    IPv6Address = ipv6Info?.Address.ToString(),
                    MacAddress = macAddress,
                    IsUp = isUp,
                    IsLoopback = isLoopback,
                    SupportsMulticast = iface.SupportsMulticast,
                    InterfaceKind = kind,
                    SpeedBytesPerSec = iface.Speed,
                    UnusableReason = unusableReason
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "System network enumeration failed; attempting fallback via ifconfig");
        }

        if (result.Count == 0)
        {
            TryEnumerateViaIfconfig(result, includeLoopback: true);
        }

        return result
            .OrderBy(i => !i.IsUsable)
            .ThenBy(i => i.IsLoopback)
            .ThenBy(i => i.Name)
            .ToList();
    }

    public IReadOnlyList<NetworkInterfaceInfo> GetEligibleInterfaces(bool includeLoopback = false)
    {
        var all = GetAllInterfaces();
        var eligible = all.Where(i =>
        {
            if (i.IsLoopback) return includeLoopback;
            return i.IsUsable;
        }).ToList();

        if (eligible.Count == 0 && includeLoopback)
        {
            var loopback = all.FirstOrDefault(i => i.IsLoopback);
            if (loopback != null)
            {
                eligible.Add(loopback);
            }
            else
            {
                eligible.Add(new NetworkInterfaceInfo
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
        }

        return eligible;
    }

    private string? RunNetworkSetupHardwarePortsWithTimeout(TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/networksetup",
                Arguments = "-listallhardwareports",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;

            using var cts = new CancellationTokenSource(timeout);
            var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            if (readTask.Wait(timeout))
            {
                process.WaitForExit(500);
                return readTask.Result;
            }

            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch
        {
            return null;
        }
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
