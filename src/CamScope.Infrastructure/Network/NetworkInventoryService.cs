using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkInventoryService : INetworkInventoryService
{
    private readonly ILogger<NetworkInventoryService> _logger;
    private readonly INetworkPermissionService _permissionService;
    private readonly TimeSpan _processTimeout;
    private readonly object _cacheLock = new();
    private NetworkInventoryReport? _cachedReport;
    private DateTime _cacheExpirationUtc = DateTime.MinValue;

    public NetworkInventoryService(
        ILogger<NetworkInventoryService> logger,
        INetworkPermissionService permissionService,
        TimeSpan? processTimeout = null)
    {
        _logger = logger;
        _permissionService = permissionService;
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<NetworkInventoryReport> GetNetworkInventoryAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        lock (_cacheLock)
        {
            if (!forceRefresh && _cachedReport != null && DateTime.UtcNow < _cacheExpirationUtc)
            {
                return _cachedReport;
            }
        }

        var report = await BuildInventoryReportAsync(ct).ConfigureAwait(false);

        lock (_cacheLock)
        {
            _cachedReport = report;
            _cacheExpirationUtc = DateTime.UtcNow.AddSeconds(10);
        }

        return report;
    }

    public async Task<NetworkRoutingSnapshot> CaptureRoutingSnapshotAsync(CancellationToken ct = default)
    {
        var (defaultGw, primaryIface) = await DetectDefaultRouteAsync(ct).ConfigureAwait(false);
        var dnsServers = GetSystemDnsServers();
        string? currentSsid = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var wifiOutput = await RunProcessAsync("system_profiler", "SPAirPortDataType", ct).ConfigureAwait(false);
                var (ssid, _) = ParseAirPortOutput(wifiOutput);
                currentSsid = ssid;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to capture current SSID for routing snapshot");
            }
        }

        return new NetworkRoutingSnapshot
        {
            DefaultGateway = defaultGw,
            PrimaryInterfaceName = primaryIface,
            DnsServers = dnsServers,
            CurrentWifiSsid = currentSsid,
            CapturedAtUtc = DateTime.UtcNow
        };
    }

    public bool VerifyRoutingInvariant(NetworkRoutingSnapshot before, NetworkRoutingSnapshot after, out string? mutationWarning)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!string.Equals(before.DefaultGateway, after.DefaultGateway, StringComparison.OrdinalIgnoreCase))
        {
            mutationWarning = $"Default gateway mutated! Before: '{before.DefaultGateway}', After: '{after.DefaultGateway}'. System connection may have been altered.";
            return false;
        }

        if (!string.Equals(before.PrimaryInterfaceName, after.PrimaryInterfaceName, StringComparison.OrdinalIgnoreCase))
        {
            mutationWarning = $"Primary network interface mutated! Before: '{before.PrimaryInterfaceName}', After: '{after.PrimaryInterfaceName}'.";
            return false;
        }

        mutationWarning = null;
        return true;
    }

    private async Task<NetworkInventoryReport> BuildInventoryReportAsync(CancellationToken ct)
    {
        var hwPorts = new Dictionary<string, (string Port, string Mac)>(StringComparer.OrdinalIgnoreCase);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var hwOutput = await RunProcessAsync("networksetup", "-listallhardwareports", ct).ConfigureAwait(false);
                hwPorts = ParseHardwarePortsOutput(hwOutput);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to retrieve macOS hardware ports via networksetup");
            }
        }

        var (defaultGw, primaryIfaceName) = await DetectDefaultRouteAsync(ct).ConfigureAwait(false);
        var dnsServers = GetSystemDnsServers();

        var interfaces = new List<NetworkInterfaceInfo>();
        try
        {
            var allAdapters = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var iface in allAdapters)
            {
                var ipProps = iface.GetIPProperties();
                var ipv4Info = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                var ipv6Info = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6);

                string ipStr = ipv4Info?.Address.ToString() ?? string.Empty;
                string netmask = ipv4Info?.IPv4Mask?.ToString() ?? "255.255.255.0";
                bool isLoopback = iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                                 ipStr.StartsWith("127.", StringComparison.Ordinal);

                // Check hardware port mapping
                hwPorts.TryGetValue(iface.Name, out var hwInfo);
                string portName = hwInfo.Port ?? string.Empty;
                string macAddress = string.IsNullOrWhiteSpace(hwInfo.Mac)
                    ? FormatMacAddress(iface.GetPhysicalAddress().GetAddressBytes())
                    : hwInfo.Mac;

                // Determine interface kind
                var kind = ClassifyInterfaceKind(iface.Name, portName, iface.Description, iface.NetworkInterfaceType, isLoopback);

                // Determine gateway
                string? gateway = null;
                if (iface.Name.Equals(primaryIfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    gateway = defaultGw;
                }
                else
                {
                    var gwAddr = ipProps.GatewayAddresses.FirstOrDefault()?.Address;
                    if (gwAddr != null && !gwAddr.Equals(IPAddress.Any) && !gwAddr.Equals(IPAddress.None))
                    {
                        gateway = gwAddr.ToString();
                    }
                }

                bool isPrimary = !string.IsNullOrEmpty(primaryIfaceName) &&
                                 iface.Name.Equals(primaryIfaceName, StringComparison.OrdinalIgnoreCase);

                interfaces.Add(new NetworkInterfaceInfo
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
                    IsUp = iface.OperationalStatus == OperationalStatus.Up,
                    SupportsMulticast = iface.SupportsMulticast,
                    IsLoopback = isLoopback,
                    IsPrimaryInternet = isPrimary,
                    InterfaceKind = kind,
                    SpeedBytesPerSec = iface.Speed
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate .NET network interfaces");
        }

        // Wi-Fi networks enumeration
        var wifiNetworks = new List<WifiNetworkInfo>();
        string? currentWifiSsid = null;
        bool isWifiPermitted = await _permissionService.HasWifiScanningPermissionAsync().ConfigureAwait(false);
        string? permissionExplanation = isWifiPermitted ? null : _permissionService.GetPermissionExplanation();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && isWifiPermitted)
        {
            try
            {
                var wifiOutput = await RunProcessAsync("system_profiler", "SPAirPortDataType", ct).ConfigureAwait(false);
                var (currSsid, networks) = ParseAirPortOutput(wifiOutput);
                currentWifiSsid = currSsid;
                wifiNetworks = networks;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to scan Wi-Fi networks via system_profiler");
            }
        }

        var primaryIface = interfaces.FirstOrDefault(i => i.IsPrimaryInternet) ??
                           interfaces.FirstOrDefault(i => i.IsUp && !i.IsLoopback && !string.IsNullOrEmpty(i.IPv4Address));

        return new NetworkInventoryReport
        {
            Timestamp = DateTime.UtcNow,
            Interfaces = interfaces.OrderByDescending(i => i.IsPrimaryInternet)
                                   .ThenBy(i => i.IsLoopback)
                                   .ThenBy(i => i.Name)
                                   .ToList(),
            PrimaryInterface = primaryIface,
            WifiNetworks = wifiNetworks,
            CurrentWifiSsid = currentWifiSsid,
            IsWifiPermitted = isWifiPermitted,
            PermissionMessage = permissionExplanation,
            SystemDnsServers = dnsServers,
            SystemDefaultGateway = defaultGw
        };
    }

    private async Task<(string? Gateway, string? InterfaceName)> DetectDefaultRouteAsync(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var netstatOutput = await RunProcessAsync("netstat", "-rn -f inet", ct).ConfigureAwait(false);
                var parsed = ParseDefaultRouteOutput(netstatOutput);
                if (!string.IsNullOrEmpty(parsed.Gateway))
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect default route via netstat");
            }
        }

        // Fallback to .NET GatewayAddresses
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipProps = iface.GetIPProperties();
                var gw = ipProps.GatewayAddresses.FirstOrDefault()?.Address;
                if (gw != null && !gw.Equals(IPAddress.Any) && !gw.Equals(IPAddress.None))
                {
                    return (gw.ToString(), iface.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fallback default route detection failed");
        }

        return (null, null);
    }

    private static IReadOnlyList<string> GetSystemDnsServers()
    {
        var dnsList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var dns in iface.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(dns))
                    {
                        dnsList.Add(dns.ToString());
                    }
                }
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        if (dnsList.Count == 0 && File.Exists("/etc/resolv.conf"))
        {
            try
            {
                foreach (var line in File.ReadAllLines("/etc/resolv.conf"))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && IPAddress.TryParse(parts[1], out _))
                        {
                            dnsList.Add(parts[1]);
                        }
                    }
                }
            }
            catch
            {
                // Ignore fallback errors
            }
        }

        return dnsList.ToList();
    }

    public static NetworkInterfaceKind ClassifyInterfaceKind(
        string name,
        string hardwarePort,
        string description,
        NetworkInterfaceType type,
        bool isLoopback)
    {
        if (isLoopback || name.StartsWith("lo", StringComparison.OrdinalIgnoreCase))
            return NetworkInterfaceKind.Loopback;

        var combined = $"{hardwarePort} {description}".Trim();

        if (combined.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("AirPort", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            type == NetworkInterfaceType.Wireless80211)
        {
            return NetworkInterfaceKind.WiFi;
        }

        if (combined.Contains("Thunderbolt Bridge", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("bridge", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkInterfaceKind.Virtual;
        }

        if (combined.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase))
            return NetworkInterfaceKind.Thunderbolt;

        if (name.StartsWith("utun", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("tap", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Tunnel", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkInterfaceKind.Vpn;
        }

        if (combined.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            (combined.Contains("Ethernet Adapter (en", StringComparison.OrdinalIgnoreCase) &&
             !name.Equals("en0", StringComparison.OrdinalIgnoreCase)))
        {
            return NetworkInterfaceKind.UsbEthernet;
        }

        if (combined.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
            type == NetworkInterfaceType.Ethernet)
        {
            return NetworkInterfaceKind.Ethernet;
        }

        return NetworkInterfaceKind.Other;
    }

    public static Dictionary<string, (string Port, string Mac)> ParseHardwarePortsOutput(string text)
    {
        var result = new Dictionary<string, (string Port, string Mac)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var blocks = text.Split(["Hardware Port:"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;

            string port = lines[0].Trim();
            string? device = null;
            string? mac = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Device:", StringComparison.OrdinalIgnoreCase))
                {
                    device = trimmed["Device:".Length..].Trim();
                }
                else if (trimmed.StartsWith("Ethernet Address:", StringComparison.OrdinalIgnoreCase))
                {
                    mac = trimmed["Ethernet Address:".Length..].Trim();
                }
            }

            if (!string.IsNullOrEmpty(device))
            {
                result[device] = (port, mac ?? string.Empty);
            }
        }

        return result;
    }

    public static (string? Gateway, string? InterfaceName) ParseDefaultRouteOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("default", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    string gateway = parts[1];
                    string iface = parts[^1];
                    // On macOS netstat -rn: "default <gateway> <flags> <netif> [expire]"
                    // e.g. "default 192.168.31.1 UGScg en0"
                    if (parts.Length == 4)
                    {
                        iface = parts[3];
                    }
                    else if (parts.Length >= 5)
                    {
                        iface = parts[3];
                    }

                    return (gateway, iface);
                }
            }
        }

        return (null, null);
    }

    public static (string? CurrentSsid, List<WifiNetworkInfo> Networks) ParseAirPortOutput(string text)
    {
        var networks = new List<WifiNetworkInfo>();
        string? currentSsid = null;

        if (string.IsNullOrWhiteSpace(text))
            return (currentSsid, networks);

        var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
        bool inCurrentNetwork = false;
        bool inOtherNetworks = false;
        WifiNetworkInfo? currentEntry = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (trimmed.StartsWith("Current Network Information:", StringComparison.OrdinalIgnoreCase))
            {
                inCurrentNetwork = true;
                inOtherNetworks = false;
                continue;
            }

            if (trimmed.StartsWith("Other Local Wi-Fi Networks:", StringComparison.OrdinalIgnoreCase))
            {
                inCurrentNetwork = false;
                inOtherNetworks = true;
                continue;
            }

            if (trimmed.StartsWith("Interfaces:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Software Versions:", StringComparison.OrdinalIgnoreCase))
            {
                inCurrentNetwork = false;
                inOtherNetworks = false;
                continue;
            }

            if (!inCurrentNetwork && !inOtherNetworks)
                continue;

            // Check if this line introduces a new SSID: typically 12 spaces in Current Network or 12 spaces in Other Local
            // e.g. "            MyHomeNetwork:"
            int leadingSpaces = rawLine.TakeWhile(c => c == ' ').Count();

            if (trimmed.EndsWith(':') &&
                !trimmed.StartsWith("PHY Mode:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Channel:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Security:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Network Type:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Signal / Noise:", StringComparison.OrdinalIgnoreCase) &&
                leadingSpaces is >= 10 and <= 14)
            {
                string ssid = trimmed[..^1].Trim();
                if (!string.IsNullOrEmpty(ssid))
                {
                    currentEntry = new WifiNetworkInfo
                    {
                        Ssid = ssid,
                        IsCurrent = inCurrentNetwork,
                        Rssi = inCurrentNetwork ? -50 : -65 // Default baseline if not explicitly parsed
                    };

                    if (inCurrentNetwork)
                    {
                        currentSsid = ssid;
                    }

                    networks.Add(currentEntry);
                }
                continue;
            }

            if (currentEntry == null)
                continue;

            // Parse attributes of the current entry
            if (trimmed.StartsWith("PHY Mode:", StringComparison.OrdinalIgnoreCase))
            {
                currentEntry.PhyMode = trimmed["PHY Mode:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Security:", StringComparison.OrdinalIgnoreCase))
            {
                currentEntry.Security = trimmed["Security:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Channel:", StringComparison.OrdinalIgnoreCase))
            {
                var channelText = trimmed["Channel:".Length..].Trim();
                var match = Regex.Match(channelText, @"^(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int ch))
                {
                    currentEntry.Channel = ch;
                }

                if (channelText.Contains("5GHz", StringComparison.OrdinalIgnoreCase))
                    currentEntry.Band = "5 GHz";
                else if (channelText.Contains("2GHz", StringComparison.OrdinalIgnoreCase) || channelText.Contains("2.4GHz", StringComparison.OrdinalIgnoreCase))
                    currentEntry.Band = "2.4 GHz";
                else if (channelText.Contains("6GHz", StringComparison.OrdinalIgnoreCase))
                    currentEntry.Band = "6 GHz";
            }
            else if (trimmed.StartsWith("Signal / Noise:", StringComparison.OrdinalIgnoreCase))
            {
                var sigMatch = Regex.Match(trimmed, @"(-\d+)\s*dBm");
                if (sigMatch.Success && int.TryParse(sigMatch.Groups[1].Value, out int rssi))
                {
                    currentEntry.Rssi = rssi;
                }
            }
        }

        // Deduplicate by SSID, preserving IsCurrent if duplicate
        var deduped = networks
            .GroupBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(n => n.IsCurrent).ThenByDescending(n => n.Rssi).First())
            .OrderByDescending(n => n.IsCurrent)
            .ThenByDescending(n => n.Rssi)
            .ToList();

        return (currentSsid, deduped);
    }

    private static string FormatMacAddress(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 6)
            return string.Empty;

        return string.Join(":", bytes.Select(b => b.ToString("x2")));
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_processTimeout);

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return await outputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command '{fileName} {arguments}' timed out after {_processTimeout.TotalSeconds}s");
        }
    }
}
