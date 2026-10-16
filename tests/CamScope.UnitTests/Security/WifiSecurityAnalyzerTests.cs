using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Models;
using Xunit;

namespace CamScope.UnitTests.Security;

public class WifiSecurityAnalyzerTests
{
    private readonly WifiSecurityAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_OpenNetwork_FlagsHighSeverityWithActionableRemediation()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "CoffeeShop_Free",
            Bssid = "00:11:22:33:44:55",
            SecurityStandard = WifiSecurityStandard.Open,
            RawSecurityString = "Open",
            Channel = 6,
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([ap]);

        Assert.Equal(1, report.TotalOpenNetworks);
        Assert.Equal(SecurityPostureLevel.Vulnerable, report.OverallPosture);
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.High && f.Title.Contains("Unencrypted"));
        var openFinding = report.Findings.First(f => f.Severity == FindingSeverity.High);
        Assert.Contains("zero link-layer encryption", openFinding.EvidenceObserved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WPA2", openFinding.RecommendedRemediation);
        Assert.False(openFinding.RequiresRouterSideVerification);
    }

    [Fact]
    public void Analyze_WepNetwork_FlagsCriticalSeverityAndDeprecation()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "LegacyWarehouse",
            Bssid = "00:11:22:33:44:66",
            SecurityStandard = WifiSecurityStandard.Wep,
            RawSecurityString = "WEP",
            Channel = 1,
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([ap]);

        Assert.Equal(SecurityPostureLevel.CriticalRisk, report.OverallPosture);
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.Critical && f.Title.Contains("Deprecated WEP"));
    }

    [Fact]
    public void Analyze_Wpa1Network_FlagsHighSeverityTkipVulnerability()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "OldRouter",
            Bssid = "00:11:22:33:44:77",
            SecurityStandard = WifiSecurityStandard.WpaPersonal,
            RawSecurityString = "WPA Personal (TKIP)",
            Channel = 11,
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([ap]);

        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.High && f.Title.Contains("WPA1 / TKIP"));
    }

    [Fact]
    public void Analyze_Wpa2Network_MarksPmfAsRequiringRouterVerification()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "SecureOffice",
            Bssid = "00:11:22:33:44:88",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            RawSecurityString = "WPA2 Personal (AES)",
            Channel = 36,
            Band = "5 GHz",
            PmfCapability = PmfStatus.UnknownNotObservable
        };

        var report = _analyzer.Analyze([ap]);

        var pmfFinding = report.Findings.FirstOrDefault(f => f.Title.Contains("Protected Management Frames"));
        Assert.NotNull(pmfFinding);
        Assert.True(pmfFinding.RequiresRouterSideVerification);
        Assert.Equal(FindingConfidence.UnobservableRouterSideRequired, pmfFinding.Confidence);
        Assert.Equal(FindingSeverity.Informational, pmfFinding.Severity);
    }

    [Fact]
    public void Analyze_Wpa3Network_AcknowledgesMandatoryPmfAndSatisfactoryPosture()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "Enterprise_WPA3",
            Bssid = "00:11:22:33:44:99",
            SecurityStandard = WifiSecurityStandard.Wpa3Personal,
            RawSecurityString = "WPA3 Personal (SAE)",
            Channel = 149,
            Band = "5 GHz",
            PmfCapability = PmfStatus.Mandatory
        };

        var report = _analyzer.Analyze([ap]);

        Assert.Equal(1, report.TotalWpa3Networks);
        Assert.Equal(0, report.CriticalFindingsCount);
        Assert.Equal(0, report.HighFindingsCount);
        Assert.Equal(SecurityPostureLevel.Secure, report.OverallPosture);
    }

    [Fact]
    public void Analyze_WpsEnabled_FlagsMediumSeverityWpsPinVulnerability()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "HomeRouter",
            Bssid = "00:11:22:33:44:AA",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            SupportsWps = true,
            Channel = 6,
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([ap]);

        var wpsFinding = report.Findings.FirstOrDefault(f => f.Title.Contains("WPS"));
        Assert.NotNull(wpsFinding);
        Assert.Equal(FindingSeverity.Medium, wpsFinding.Severity);
        Assert.Contains("Pixie Dust", wpsFinding.ImpactExplanation);
        Assert.Contains("Disable WPS", wpsFinding.RecommendedRemediation);
    }

    [Fact]
    public void Analyze_DuplicateSsidWithMatchingSecurity_ClassifiedAsLegitimateMesh()
    {
        var ap1 = new WifiAccessPointObservation
        {
            Ssid = "CampusMesh",
            Bssid = "00:11:22:33:44:01",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            Channel = 1,
            Band = "2.4 GHz"
        };
        var ap2 = new WifiAccessPointObservation
        {
            Ssid = "CampusMesh",
            Bssid = "00:11:22:33:44:02",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            Channel = 36,
            Band = "5 GHz"
        };

        var report = _analyzer.Analyze([ap1, ap2]);

        Assert.Single(report.DuplicateSsidAnalyses);
        var dup = report.DuplicateSsidAnalyses[0];
        Assert.Equal("CampusMesh", dup.Ssid);
        Assert.False(dup.HasSecurityMismatch);
        Assert.False(dup.HasRogueRisk);
        Assert.Contains("mesh", dup.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_DuplicateSsidWithSecurityMismatch_FlagsRogueRisk()
    {
        var legitimateAp = new WifiAccessPointObservation
        {
            Ssid = "Corporate_Staff",
            Bssid = "00:11:22:33:44:01",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            Channel = 36,
            Band = "5 GHz"
        };
        var rogueAp = new WifiAccessPointObservation
        {
            Ssid = "Corporate_Staff",
            Bssid = "00:99:88:77:66:55",
            SecurityStandard = WifiSecurityStandard.Open,
            Channel = 6,
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([legitimateAp, rogueAp]);

        var dup = report.DuplicateSsidAnalyses.FirstOrDefault(d => d.Ssid == "Corporate_Staff");
        Assert.NotNull(dup);
        Assert.True(dup.HasSecurityMismatch);
        Assert.True(dup.HasRogueRisk);
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.High && f.Title.Contains("Security Mismatch"));
    }

    [Fact]
    public void Analyze_NonStandard24GhzChannel_GeneratesAdjacentInterferenceFinding()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "InterferingAP",
            Bssid = "00:11:22:33:44:03",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal,
            Channel = 4, // Non-standard 2.4 GHz channel (standard is 1, 6, 11)
            Band = "2.4 GHz"
        };

        var report = _analyzer.Analyze([ap]);

        var interferenceFinding = report.Findings.FirstOrDefault(f => f.Title.Contains("Non-Standard 2.4 GHz"));
        Assert.NotNull(interferenceFinding);
        Assert.Equal(FindingSeverity.Informational, interferenceFinding.Severity);
        Assert.Contains("1, 6, or 11", interferenceFinding.RecommendedRemediation);
    }
}
