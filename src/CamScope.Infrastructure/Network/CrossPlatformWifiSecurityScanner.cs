using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class WindowsWifiSecurityScanner : IWifiSecurityScanner
{
    private readonly ILogger<WindowsWifiSecurityScanner>? _logger;

    public bool IsPlatformSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public WindowsWifiSecurityScanner(ILogger<WindowsWifiSecurityScanner>? logger = null)
    {
        _logger = logger;
    }

    public async Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
    {
        if (!IsPlatformSupported)
        {
            return new WifiScanSessionResult
            {
                Succeeded = false,
                PlatformSupported = false,
                ErrorMessage = "Windows Wi-Fi scanning is only supported on Windows OS.",
                ScannedAtUtc = DateTime.UtcNow
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show networks mode=bssid",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new WifiScanSessionResult
                {
                    Succeeded = false,
                    ErrorMessage = "Failed to launch netsh process.",
                    ScannedAtUtc = DateTime.UtcNow
                };
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var observations = ParseNetshBssidOutput(output);
            return new WifiScanSessionResult
            {
                Succeeded = true,
                Observations = observations,
                ScannedAtUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to execute netsh Wi-Fi scan on Windows");
            return new WifiScanSessionResult
            {
                Succeeded = false,
                ErrorMessage = $"Windows scan failed: {ex.Message}",
                ScannedAtUtc = DateTime.UtcNow
            };
        }
    }

    public static List<WifiAccessPointObservation> ParseNetshBssidOutput(string output)
    {
        var list = new List<WifiAccessPointObservation>();
        if (string.IsNullOrWhiteSpace(output)) return list;

        string currentSsid = string.Empty;
        string currentAuth = "Unknown";
        string currentRadio = "802.11ax";
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                currentSsid = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                currentAuth = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (line.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                currentRadio = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                var bssid = line[(line.IndexOf(':') + 1)..].Trim();
                var standard = currentAuth.Contains("WPA3", StringComparison.OrdinalIgnoreCase)
                    ? WifiSecurityStandard.Wpa3Personal
                    : currentAuth.Contains("WPA2", StringComparison.OrdinalIgnoreCase)
                        ? WifiSecurityStandard.Wpa2Personal
                        : currentAuth.Contains("Open", StringComparison.OrdinalIgnoreCase)
                            ? WifiSecurityStandard.Open
                            : WifiSecurityStandard.Unknown;

                list.Add(new WifiAccessPointObservation
                {
                    Ssid = currentSsid,
                    Bssid = bssid,
                    SecurityStandard = standard,
                    RawSecurityString = currentAuth,
                    PhyMode = currentRadio,
                    PmfCapability = standard == WifiSecurityStandard.Wpa3Personal ? PmfStatus.Mandatory : PmfStatus.UnknownNotObservable,
                    IsHidden = string.IsNullOrEmpty(currentSsid)
                });
            }
            else if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) && line.Contains(':') && list.Count > 0)
            {
                var sigMatch = Regex.Match(line, @"(\d+)%");
                if (sigMatch.Success && int.TryParse(sigMatch.Groups[1].Value, out int percent))
                {
                    list[^1].Rssi = (percent / 2) - 100;
                }
            }
            else if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) && line.Contains(':') && list.Count > 0)
            {
                var chMatch = Regex.Match(line, @"(\d+)");
                if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, out int ch))
                {
                    list[^1].Channel = ch;
                    list[^1].Band = ch > 14 ? "5 GHz" : "2.4 GHz";
                }
            }
        }

        return list;
    }
}

public sealed class LinuxWifiSecurityScanner : IWifiSecurityScanner
{
    private readonly ILogger<LinuxWifiSecurityScanner>? _logger;

    public bool IsPlatformSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public LinuxWifiSecurityScanner(ILogger<LinuxWifiSecurityScanner>? logger = null)
    {
        _logger = logger;
    }

    public Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new WifiScanSessionResult
        {
            Succeeded = false,
            PlatformSupported = IsPlatformSupported,
            ErrorMessage = "Linux Wi-Fi security scanner requires nmcli or iw configuration on this host.",
            ScannedAtUtc = DateTime.UtcNow
        });
    }
}

public sealed class UnsupportedWifiSecurityScanner : IWifiSecurityScanner
{
    public bool IsPlatformSupported => false;

    public Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new WifiScanSessionResult
        {
            Succeeded = false,
            PlatformSupported = false,
            ErrorMessage = "Wi-Fi security scanning is not supported on this platform.",
            ScannedAtUtc = DateTime.UtcNow
        });
    }
}

public sealed class SimulatedWifiSecurityScanner : IWifiSecurityScanner
{
    public bool IsPlatformSupported => true;

    public Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
    {
        var sampleObservations = new List<WifiAccessPointObservation>
        {
            new()
            {
                Ssid = "Office_Corporate_5G",
                Bssid = "00:14:22:01:23:45",
                Rssi = -42,
                Channel = 36,
                Band = "5 GHz",
                PhyMode = "802.11ax (Wi-Fi 6)",
                SecurityStandard = WifiSecurityStandard.Wpa3Personal,
                RawSecurityString = "WPA3 Personal",
                SupportsWps = false,
                PmfCapability = PmfStatus.Mandatory,
                IsCurrent = true
            },
            new()
            {
                Ssid = "Guest_Access_Open",
                Bssid = "00:14:22:01:23:46",
                Rssi = -65,
                Channel = 6,
                Band = "2.4 GHz",
                PhyMode = "802.11ac (Wi-Fi 5)",
                SecurityStandard = WifiSecurityStandard.Open,
                RawSecurityString = "Open",
                SupportsWps = false,
                PmfCapability = PmfStatus.Disabled
            },
            new()
            {
                Ssid = "Legacy_CCTV_AP",
                Bssid = "00:0C:43:AA:BB:CC",
                Rssi = -70,
                Channel = 11,
                Band = "2.4 GHz",
                PhyMode = "802.11g",
                SecurityStandard = WifiSecurityStandard.Wep,
                RawSecurityString = "WEP",
                SupportsWps = false,
                PmfCapability = PmfStatus.Disabled
            },
            new()
            {
                Ssid = "Warehouse_IoT",
                Bssid = "70:3A:0E:11:22:33",
                Rssi = -58,
                Channel = 149,
                Band = "5 GHz",
                PhyMode = "802.11ax (Wi-Fi 6)",
                SecurityStandard = WifiSecurityStandard.Wpa2Wpa3Transition,
                RawSecurityString = "WPA2/WPA3 Transition",
                SupportsWps = true, // WPS enabled
                PmfCapability = PmfStatus.Capable
            },
            new()
            {
                Ssid = "Warehouse_IoT",
                Bssid = "70:3A:0E:11:22:34", // Duplicate SSID with standard WPA2
                Rssi = -60,
                Channel = 157,
                Band = "5 GHz",
                PhyMode = "802.11ax (Wi-Fi 6)",
                SecurityStandard = WifiSecurityStandard.Wpa2Wpa3Transition,
                RawSecurityString = "WPA2/WPA3 Transition",
                SupportsWps = true,
                PmfCapability = PmfStatus.Capable
            },
            new()
            {
                Ssid = string.Empty, // Hidden Network
                Bssid = "D8:07:B6:99:88:77",
                Rssi = -75,
                Channel = 40,
                Band = "5 GHz",
                PhyMode = "802.11ax (Wi-Fi 6)",
                SecurityStandard = WifiSecurityStandard.Wpa2Personal,
                RawSecurityString = "WPA2 Personal",
                SupportsWps = false,
                PmfCapability = PmfStatus.UnknownNotObservable,
                IsHidden = true
            },
            new()
            {
                Ssid = "Neighbour_Overlapping",
                Bssid = "A4:91:B1:44:55:66",
                Rssi = -80,
                Channel = 3, // Non-standard 2.4 GHz channel
                Band = "2.4 GHz",
                PhyMode = "802.11n (Wi-Fi 4)",
                SecurityStandard = WifiSecurityStandard.Wpa2Personal,
                RawSecurityString = "WPA2 Personal",
                SupportsWps = false,
                PmfCapability = PmfStatus.UnknownNotObservable
            }
        };

        return Task.FromResult(new WifiScanSessionResult
        {
            Succeeded = true,
            PlatformSupported = true,
            IsSimulated = true,
            Observations = sampleObservations,
            ScannedAtUtc = DateTime.UtcNow
        });
    }
}
