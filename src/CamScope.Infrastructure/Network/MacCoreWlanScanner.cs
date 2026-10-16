using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class MacCoreWlanScanner
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _timeout;

    private const string SwiftScanScript = @"
import CoreWLAN
import Foundation

struct WifiInfo: Codable {
    let ssid: String
    let rssi: Int
    let channel: Int
    let band: String
    let security: String
    let bssid: String?
}

var results: [WifiInfo] = []

func getSecurityString(_ net: CWNetwork) -> String {
    if net.supportsSecurity(.wpa3Personal) { return ""WPA3 Personal"" }
    if net.supportsSecurity(.wpa3Enterprise) { return ""WPA3 Enterprise"" }
    if net.supportsSecurity(.wpa2Personal) { return ""WPA2 Personal"" }
    if net.supportsSecurity(.wpa2Enterprise) { return ""WPA2 Enterprise"" }
    if net.supportsSecurity(.wpaPersonal) { return ""WPA Personal"" }
    if net.supportsSecurity(.wpaEnterprise) { return ""WPA Enterprise"" }
    if net.supportsSecurity(.none) { return ""Open"" }
    return ""Unknown""
}

func getBandString(_ band: CWChannelBand) -> String {
    switch band {
    case .band2GHz: return ""2.4 GHz""
    case .band5GHz: return ""5 GHz""
    case .band6GHz: return ""6 GHz""
    default: return ""Unknown""
    }
}

let wifiClient = CWWiFiClient.shared()
let iface = wifiClient.interface()
if let iface = iface, let networks = try? iface.scanForNetworks(withName: nil) {
    for net in networks {
        guard let ssid = net.ssid, !ssid.isEmpty, ssid != ""<redacted>"" else { continue }
        let bandStr = net.wlanChannel != nil ? getBandString(net.wlanChannel!.channelBand) : ""Unknown""
        let secStr = getSecurityString(net)
        results.append(WifiInfo(
            ssid: ssid,
            rssi: net.rssiValue,
            channel: net.wlanChannel?.channelNumber ?? 0,
            band: bandStr,
            security: secStr,
            bssid: net.bssid
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

    public MacCoreWlanScanner(ILogger? logger = null, TimeSpan? timeout = null)
    {
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && File.Exists("/usr/bin/swift");

    public async Task<List<WifiNetworkInfo>> ScanNetworksAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return [];
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
            psi.ArgumentList.Add(SwiftScanScript);

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
                _logger?.LogDebug("CoreWLAN scan via swift returned non-zero exit code {Code}: {Err}", process.ExitCode, stderr);
                return [];
            }

            return ParseScanJson(stdout);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("CoreWLAN swift scan timed out after {Sec}s", _timeout.TotalSeconds);
            return [];
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to execute CoreWLAN swift scan");
            return [];
        }
    }

    public static List<WifiNetworkInfo> ParseScanJson(string json)
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

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var entries = JsonSerializer.Deserialize<List<CoreWlanEntryDto>>(validJson, options);
            if (entries == null || entries.Count == 0)
                return [];

            var results = new List<WifiNetworkInfo>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Ssid) ||
                    entry.Ssid.Equals("<redacted>", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new WifiNetworkInfo
                {
                    Ssid = entry.Ssid.Trim(),
                    Rssi = entry.Rssi,
                    Channel = entry.Channel,
                    Band = string.IsNullOrWhiteSpace(entry.Band) ? null : entry.Band,
                    Security = string.IsNullOrWhiteSpace(entry.Security) ? "Open" : entry.Security,
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

    private sealed class CoreWlanEntryDto
    {
        [JsonPropertyName("ssid")]
        public string? Ssid { get; set; }

        [JsonPropertyName("rssi")]
        public int Rssi { get; set; }

        [JsonPropertyName("channel")]
        public int Channel { get; set; }

        [JsonPropertyName("band")]
        public string? Band { get; set; }

        [JsonPropertyName("security")]
        public string? Security { get; set; }

        [JsonPropertyName("bssid")]
        public string? Bssid { get; set; }
    }
}
