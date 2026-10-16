using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class MacWifiSecurityScanner : IWifiSecurityScanner
{
    private readonly ILogger<MacWifiSecurityScanner>? _logger;
    private readonly TimeSpan _timeout;

    private const string SwiftSecurityScanScript = @"
import CoreWLAN
import Foundation

struct ApDto: Codable {
    let ssid: String
    let bssid: String?
    let rssi: Int
    let channel: Int
    let band: String
    let security: String
    let wps: Bool?
    let phy: String?
    let isHidden: Bool
}

var results: [ApDto] = []

func getSecurityString(_ net: CWNetwork) -> String {
    if net.supportsSecurity(.wpa3Transition) { return ""WPA2/WPA3 Transition"" }
    if net.supportsSecurity(.wpa3Personal) { return ""WPA3 Personal"" }
    if net.supportsSecurity(.wpa3Enterprise) { return ""WPA3 Enterprise"" }
    if net.supportsSecurity(.wpa2Personal) { return ""WPA2 Personal"" }
    if net.supportsSecurity(.wpa2Enterprise) { return ""WPA2 Enterprise"" }
    if net.supportsSecurity(.wpaPersonal) { return ""WPA Personal"" }
    if net.supportsSecurity(.dynamicWEP) || (CWSecurity(rawValue: 1) != nil && net.supportsSecurity(CWSecurity(rawValue: 1)!)) { return ""WEP"" }
    if net.supportsSecurity(.none) { return ""Open"" }
    return ""Unknown""
}

func getPhyString(_ modeVal: Int) -> String {
    switch modeVal {
    case 128: return ""802.11be (Wi-Fi 7)""
    case 64: return ""802.11ax (Wi-Fi 6)""
    case 16: return ""802.11ac (Wi-Fi 5)""
    case 8: return ""802.11n (Wi-Fi 4)""
    case 4: return ""802.11g""
    case 2: return ""802.11b""
    case 1: return ""802.11a""
    default: return ""Unknown PHY""
    }
}

func getBandString(_ band: CWChannelBand) -> String {
    switch band {
    case .band2GHz: return ""2.4 GHz""
    case .band5GHz: return ""5 GHz""
    case .band6GHz: return ""6 GHz""
    default: return ""Unknown""
    }
}

if let iface = CWWiFiClient.shared().interface(), let networks = try? iface.scanForNetworks(withName: nil) {
    for net in networks {
        let rawSsid = net.ssid ?? """"
        let isHidden = rawSsid.isEmpty || rawSsid == ""<redacted>""
        let rawBssid = net.bssid
        let bandStr = net.wlanChannel != nil ? getBandString(net.wlanChannel!.channelBand) : ""Unknown""
        let secStr = getSecurityString(net)
        let wpsVal = net.value(forKey: ""supportsWPS"") as? Bool
        let phyVal = (net.value(forKey: ""physicalLayerMode"") as? NSNumber)?.intValue ?? 0
        let phyStr = getPhyString(phyVal)

        results.append(ApDto(
            ssid: isHidden ? ""<Hidden Network>"" : rawSsid,
            bssid: (rawBssid != nil && !rawBssid!.isEmpty) ? rawBssid : nil,
            rssi: net.rssiValue,
            channel: net.wlanChannel?.channelNumber ?? 0,
            band: bandStr,
            security: secStr,
            wps: wpsVal,
            phy: phyStr,
            isHidden: isHidden
        ))
    }
}

let encoder = JSONEncoder()
if let data = try? encoder.encode(results), let json = String(data: data, encoding: .utf8) {
    print(json)
} else {
    print(""[]"")
}
";

    public bool IsPlatformSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && File.Exists("/usr/bin/swift");

    public MacWifiSecurityScanner(ILogger<MacWifiSecurityScanner>? logger = null, TimeSpan? timeout = null)
    {
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
    {
        if (!IsPlatformSupported)
        {
            return new WifiScanSessionResult
            {
                Succeeded = false,
                PlatformSupported = false,
                ErrorMessage = "Passive Wi-Fi security scanning via CoreWLAN is supported on macOS.",
                ScannedAtUtc = DateTime.UtcNow
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/swift",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(SwiftSecurityScanScript);

            using var process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await outputTask.ConfigureAwait(false);
            var stderr = await errTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger?.LogWarning("CoreWLAN security scan exited with code {Code}: {Err}", process.ExitCode, stderr);
                return new WifiScanSessionResult
                {
                    Succeeded = false,
                    PlatformSupported = true,
                    ErrorMessage = $"CoreWLAN scan returned exit code {process.ExitCode}: {stderr}",
                    ScannedAtUtc = DateTime.UtcNow
                };
            }

            var observations = ParseScanJson(stdout);
            return new WifiScanSessionResult
            {
                Succeeded = true,
                PlatformSupported = true,
                Observations = observations,
                ScannedAtUtc = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Wi-Fi security scan cancelled or timed out after {Sec}s", _timeout.TotalSeconds);
            return new WifiScanSessionResult
            {
                Succeeded = false,
                PlatformSupported = true,
                ErrorMessage = "Wi-Fi security scan was cancelled or timed out waiting for access point beacons.",
                ScannedAtUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error executing CoreWLAN security scan");
            return new WifiScanSessionResult
            {
                Succeeded = false,
                PlatformSupported = true,
                ErrorMessage = $"Scan execution failed: {ex.Message}",
                ScannedAtUtc = DateTime.UtcNow
            };
        }
    }

    public static List<WifiAccessPointObservation> ParseScanJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            int startIndex = json.IndexOf('[');
            int endIndex = json.LastIndexOf(']');
            if (startIndex < 0 || endIndex < startIndex)
                return [];

            string validJson = json.Substring(startIndex, endIndex - startIndex + 1);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var dtos = JsonSerializer.Deserialize<List<CoreWlanApDto>>(validJson, options);

            if (dtos == null || dtos.Count == 0)
                return [];

            var results = new List<WifiAccessPointObservation>();
            foreach (var dto in dtos)
            {
                var standard = MapSecurityStandard(dto.Security);
                var pmf = standard switch
                {
                    WifiSecurityStandard.Wpa3Personal or WifiSecurityStandard.Wpa3Enterprise => PmfStatus.Mandatory,
                    WifiSecurityStandard.Wpa2Wpa3Transition => PmfStatus.Capable,
                    WifiSecurityStandard.Open or WifiSecurityStandard.Wep => PmfStatus.Disabled,
                    _ => PmfStatus.UnknownNotObservable
                };

                results.Add(new WifiAccessPointObservation
                {
                    Ssid = dto.Ssid ?? string.Empty,
                    Bssid = dto.Bssid,
                    Rssi = dto.Rssi,
                    Channel = dto.Channel,
                    Band = dto.Band,
                    PhyMode = dto.Phy,
                    SecurityStandard = standard,
                    RawSecurityString = dto.Security ?? "Unknown",
                    SupportsWps = dto.Wps,
                    PmfCapability = pmf,
                    IsHidden = dto.IsHidden,
                    IsCurrent = false
                });
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static WifiSecurityStandard MapSecurityStandard(string? sec)
    {
        if (string.IsNullOrWhiteSpace(sec)) return WifiSecurityStandard.Unknown;

        string s = sec.Trim();
        if (s.Contains("WPA2/WPA3", StringComparison.OrdinalIgnoreCase) || s.Contains("Transition", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wpa2Wpa3Transition;
        if (s.Contains("WPA3 Personal", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wpa3Personal;
        if (s.Contains("WPA3 Enterprise", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wpa3Enterprise;
        if (s.Contains("WPA2 Personal", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wpa2Personal;
        if (s.Contains("WPA2 Enterprise", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wpa2Enterprise;
        if (s.Contains("WPA Personal", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.WpaPersonal;
        if (s.Contains("WPA Enterprise", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.WpaEnterprise;
        if (s.Contains("WEP", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Wep;
        if (s.Contains("Open", StringComparison.OrdinalIgnoreCase) || s.Contains("None", StringComparison.OrdinalIgnoreCase))
            return WifiSecurityStandard.Open;

        return WifiSecurityStandard.Unknown;
    }

    private sealed class CoreWlanApDto
    {
        [JsonPropertyName("ssid")] public string? Ssid { get; set; }
        [JsonPropertyName("bssid")] public string? Bssid { get; set; }
        [JsonPropertyName("rssi")] public int Rssi { get; set; }
        [JsonPropertyName("channel")] public int Channel { get; set; }
        [JsonPropertyName("band")] public string? Band { get; set; }
        [JsonPropertyName("security")] public string? Security { get; set; }
        [JsonPropertyName("wps")] public bool? Wps { get; set; }
        [JsonPropertyName("phy")] public string? Phy { get; set; }
        [JsonPropertyName("isHidden")] public bool IsHidden { get; set; }
    }
}
