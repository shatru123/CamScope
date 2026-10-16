using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Xunit;

namespace CamScope.UnitTests.Network;

public class WifiSecurityScannerTests
{
    [Fact]
    public async Task SimulatedScanner_ScanAsync_ReturnsDiverseObservableProfiles()
    {
        var scanner = new SimulatedWifiSecurityScanner();
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.IsSimulated);
        Assert.NotEmpty(result.Observations);

        // Verify that simulation includes various security standards
        Assert.Contains(result.Observations, ap => ap.SecurityStandard == WifiSecurityStandard.Open);
        Assert.Contains(result.Observations, ap => ap.SecurityStandard == WifiSecurityStandard.Wpa2Personal);
        Assert.Contains(result.Observations, ap => ap.SecurityStandard == WifiSecurityStandard.Wpa3Personal);
        Assert.Contains(result.Observations, ap => ap.SecurityStandard == WifiSecurityStandard.Wpa2Wpa3Transition);
        Assert.Contains(result.Observations, ap => ap.SecurityStandard == WifiSecurityStandard.Wep);

        // Verify WPS presence
        Assert.Contains(result.Observations, ap => ap.SupportsWps == true);

        // Verify Hidden Network presence
        Assert.Contains(result.Observations, ap => ap.IsHidden);
        var hiddenAp = result.Observations.First(ap => ap.IsHidden);
        Assert.Equal("<Hidden Network>", hiddenAp.DisplaySsid);

        // Verify Duplicate SSID presence for multi-band / mesh analysis
        var groups = result.Observations.GroupBy(ap => ap.Ssid).Where(g => g.Count() > 1);
        Assert.NotEmpty(groups);
    }

    [Fact]
    public async Task UnsupportedScanner_ScanAsync_ReturnsClearFailure()
    {
        var scanner = new UnsupportedWifiSecurityScanner();
        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.PlatformSupported);
        Assert.Contains("not supported", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public void WifiAccessPointObservation_RedactedOrMissingBssid_SafelyHandledWithoutExceptions()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "TestNetwork",
            Bssid = null,
            Rssi = null
        };

        Assert.Equal("Unknown / Not Observable", ap.DisplayBssid);
        Assert.False(ap.HasSignalStrength);
        Assert.Equal(0, ap.SignalQualityPercent);
        Assert.Contains("Unknown", ap.SignalText);
    }

    [Fact]
    public void WifiAccessPointObservation_DistinctBssids_PreservedSeparately()
    {
        var ap1 = new WifiAccessPointObservation
        {
            Ssid = "MeshNode",
            Bssid = "11:22:33:44:55:66",
            Channel = 1
        };
        var ap2 = new WifiAccessPointObservation
        {
            Ssid = "MeshNode",
            Bssid = "AA:BB:CC:DD:EE:FF",
            Channel = 36
        };

        Assert.NotEqual(ap1.Id, ap2.Id);
        Assert.NotEqual(ap1.Bssid, ap2.Bssid);
        Assert.Equal(ap1.Ssid, ap2.Ssid);
    }
}
