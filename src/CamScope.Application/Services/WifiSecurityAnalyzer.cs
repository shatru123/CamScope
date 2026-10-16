using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;

namespace CamScope.Application.Services;

public sealed class WifiSecurityAnalyzer : IWifiSecurityAnalyzer
{
    private static readonly HashSet<int> Standard24GhzChannels = [1, 6, 11];

    public SecurityAssessmentReport Analyze(IReadOnlyList<WifiAccessPointObservation> observations)
    {
        var apList = observations ?? [];
        var findings = new List<SecurityFinding>();
        var duplicateAnalyses = new List<DuplicateSsidAnalysis>();
        var congestionMetrics = new List<ChannelCongestionMetric>();

        int openCount = 0;
        int wpa3Count = 0;

        // 1. Per-Access-Point Analysis
        foreach (var ap in apList)
        {
            if (ap.SecurityStandard == WifiSecurityStandard.Open ||
                ap.RawSecurityString.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                ap.RawSecurityString.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                openCount++;
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Unencrypted Wireless Network (Open)",
                    Category = "Encryption & Integrity",
                    Severity = FindingSeverity.High,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point beacon advertises zero link-layer encryption ciphers.",
                    ImpactExplanation = "All wireless frames, camera control requests, and video streaming data are transmitted in plaintext over the air, vulnerable to passive packet sniffing and injection.",
                    RecommendedRemediation = "Enable WPA3-Personal (SAE) or WPA2-Personal (AES) encryption with a strong passphrase on the router.",
                    RequiresRouterSideVerification = false
                });
            }
            else if (ap.SecurityStandard == WifiSecurityStandard.Wep ||
                     ap.RawSecurityString.Contains("WEP", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Critically Deprecated WEP Encryption Detected",
                    Category = "Legacy Encryption",
                    Severity = FindingSeverity.Critical,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point uses legacy 802.11b/g WEP encryption with 24-bit IVs and RC4 stream cipher.",
                    ImpactExplanation = "WEP encryption has known mathematical flaws. Attackers can recover the shared secret key in minutes using passive and active IV injection (PTW / FMS attacks).",
                    RecommendedRemediation = "Immediately deprecate WEP and switch to WPA2-Personal (AES-CCMP) or WPA3-Personal (SAE) on the router.",
                    RequiresRouterSideVerification = false
                });
            }
            else if (ap.SecurityStandard == WifiSecurityStandard.WpaPersonal ||
                     ap.SecurityStandard == WifiSecurityStandard.WpaEnterprise ||
                     ap.RawSecurityString.Contains("WPA Personal", StringComparison.OrdinalIgnoreCase) ||
                     ap.RawSecurityString.Contains("TKIP", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Deprecated WPA1 / TKIP Encryption Protocol",
                    Category = "Legacy Encryption",
                    Severity = FindingSeverity.High,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point advertises original WPA1 with Temporal Key Integrity Protocol (TKIP).",
                    ImpactExplanation = "TKIP is vulnerable to packet-forgery and keystream recovery attacks (Beck-Tews / Ohigashi-Morii). It has been deprecated by the Wi-Fi Alliance since 2012.",
                    RecommendedRemediation = "Reconfigure access point to disallow WPA1/TKIP and enforce WPA2-AES or WPA3-SAE only.",
                    RequiresRouterSideVerification = false
                });
            }
            else if (ap.SecurityStandard == WifiSecurityStandard.Wpa3Personal)
            {
                wpa3Count++;
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Modern WPA3-Personal (SAE) with Forward Secrecy",
                    Category = "Robust Encryption",
                    Severity = FindingSeverity.Informational,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point advertises WPA3 Simultaneous Authentication of Equals (Dragonfly handshake) with mandatory PMF.",
                    ImpactExplanation = "Resistant to offline dictionary and rainbow-table attacks. Provides forward secrecy for session traffic.",
                    RecommendedRemediation = "Maintain current configuration and keep router firmware updated.",
                    RequiresRouterSideVerification = false
                });
            }
            else if (ap.SecurityStandard == WifiSecurityStandard.Wpa2Wpa3Transition)
            {
                wpa3Count++;
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "WPA2/WPA3 Mixed Transition Mode Advertised",
                    Category = "Transition Compatibility",
                    Severity = FindingSeverity.Low,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point advertises both WPA3-SAE and legacy WPA2-PSK for client backward compatibility.",
                    ImpactExplanation = "Backward compatibility allows older devices to connect, but legacy clients using WPA2-PSK lack forward secrecy and remain exposed to offline dictionary attacks if weak passphrases are used.",
                    RecommendedRemediation = "When all connected clients support WPA3, disable legacy WPA2-PSK and enforce pure WPA3-Personal.",
                    RequiresRouterSideVerification = true
                });
            }
            else if (ap.SecurityStandard == WifiSecurityStandard.Wpa2Personal)
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Standard WPA2-Personal (PSK) Configuration",
                    Category = "Standard Encryption",
                    Severity = FindingSeverity.Low,
                    Confidence = FindingConfidence.ObservableAdvertisementOnly,
                    EvidenceObserved = "Access point advertises standard WPA2-PSK (AES-CCMP). WPA3-SAE is not advertised in beacon frames.",
                    ImpactExplanation = "Standard WPA2-Personal provides baseline encryption but lacks forward secrecy. Captured 4-way handshakes can be attacked offline if the pre-shared key has low entropy.",
                    RecommendedRemediation = "Upgrade to WPA3-Personal or WPA2/WPA3 Transition mode if supported by the router. Ensure the pre-shared key is at least 16 characters with high entropy.",
                    RequiresRouterSideVerification = true
                });
            }

            // WPS Evaluation
            if (ap.SupportsWps == true)
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Wi-Fi Protected Setup (WPS) Advertised",
                    Category = "Authentication Surface",
                    Severity = FindingSeverity.Medium,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = "Access point broadcasts WPS Information Element in beacon frames.",
                    ImpactExplanation = "WPS PIN authentication has documented architectural flaws allowing PIN brute-force and offline Pixie Dust calculation, which can reveal the WPA passphrase.",
                    RecommendedRemediation = "Disable WPS / QSS in router administration settings.",
                    RequiresRouterSideVerification = true
                });
            }

            // PMF Evaluation
            if (ap.PmfCapability == PmfStatus.UnknownNotObservable)
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = "Protected Management Frames (PMF / 802.11w) Status Unobservable",
                    Category = "Management Protection",
                    Severity = FindingSeverity.Informational,
                    Confidence = FindingConfidence.UnobservableRouterSideRequired,
                    EvidenceObserved = "Beacon frames do not explicitly advertise mandatory PMF. Client association or router inspection is required.",
                    ImpactExplanation = "Without PMF, wireless clients can be targeted by unencrypted 802.11 deauthentication and disassociation frames to induce denial-of-service.",
                    RecommendedRemediation = "Log into router administration and verify whether 802.11w / Management Frame Protection is set to Capable or Required.",
                    RequiresRouterSideVerification = true
                });
            }

            // 2.4 GHz Channel Overlap (Network Quality, NOT Vulnerability)
            if (ap.BandGroup == "2.4 GHz" && !Standard24GhzChannels.Contains(ap.Channel) && ap.Channel > 0)
            {
                findings.Add(new SecurityFinding
                {
                    AccessPointId = ap.Id,
                    Ssid = ap.DisplaySsid,
                    Bssid = ap.Bssid,
                    Title = $"Non-Standard 2.4 GHz Wireless Channel ({ap.Channel})",
                    Category = "RF & Operational Quality",
                    Severity = FindingSeverity.Informational,
                    Confidence = FindingConfidence.Confirmed,
                    EvidenceObserved = $"Access point operates on 2.4 GHz channel {ap.Channel}.",
                    ImpactExplanation = "Channels other than 1, 6, and 11 overlap with neighboring frequencies, causing co-channel and adjacent-channel RF interference. Note: This is an operational RF quality observation, not a cryptographic security vulnerability.",
                    RecommendedRemediation = "Reconfigure router 2.4 GHz radio to use standard non-overlapping channel 1, 6, or 11.",
                    RequiresRouterSideVerification = true
                });
            }
        }

        // 2. Duplicate SSID & Impersonation Pattern Analysis
        var ssidGroups = apList
            .Where(a => !string.IsNullOrWhiteSpace(a.Ssid) && !a.IsHidden)
            .GroupBy(a => a.Ssid, StringComparer.OrdinalIgnoreCase);

        foreach (var group in ssidGroups)
        {
            var distinctAps = group.ToList();
            if (distinctAps.Count > 1)
            {
                var distinctBssids = distinctAps.Select(a => a.DisplayBssid).Distinct().ToList();
                var distinctSecurities = distinctAps.Select(a => a.SecurityStandard).Distinct().ToList();
                bool hasMismatch = distinctSecurities.Count > 1;

                if (hasMismatch)
                {
                    findings.Add(new SecurityFinding
                    {
                        Ssid = group.Key,
                        Title = $"Potential Rogue AP / Security Mismatch for SSID '{group.Key}'",
                        Category = "Wireless Impersonation",
                        Severity = FindingSeverity.High,
                        Confidence = FindingConfidence.Inferred,
                        EvidenceObserved = $"Observed {distinctAps.Count} access points broadcasting identical SSID '{group.Key}' with conflicting security standards: {string.Join(", ", distinctSecurities)}.",
                        ImpactExplanation = "Mismatched security for identical SSIDs is a known indicator of Rogue AP or Evil Twin activity, where an attacker clones a legitimate network name with an unencrypted or weak configuration to intercept client connections.",
                        RecommendedRemediation = "Verify the MAC address (BSSID) of the authorized router before connecting. Investigate unexpected access points broadcasting this SSID.",
                        RequiresRouterSideVerification = true
                    });

                    duplicateAnalyses.Add(new DuplicateSsidAnalysis
                    {
                        Ssid = group.Key,
                        AccessPointsCount = distinctAps.Count,
                        DistinctBssids = distinctBssids,
                        HasSecurityMismatch = true,
                        HasRogueRisk = true,
                        Description = "Multiple access points detected with conflicting security configurations. Rogue AP risk."
                    });
                }
                else
                {
                    duplicateAnalyses.Add(new DuplicateSsidAnalysis
                    {
                        Ssid = group.Key,
                        AccessPointsCount = distinctAps.Count,
                        DistinctBssids = distinctBssids,
                        HasSecurityMismatch = false,
                        HasRogueRisk = false,
                        Description = $"Consistent multi-AP mesh or enterprise deployment across {distinctAps.Count} radios."
                    });
                }
            }
        }

        // 3. Channel Congestion Calculation
        var channelGroups = apList
            .Where(a => a.Channel > 0)
            .GroupBy(a => new { a.Channel, a.BandGroup })
            .OrderBy(g => g.Key.BandGroup)
            .ThenBy(g => g.Key.Channel);

        foreach (var cg in channelGroups)
        {
            congestionMetrics.Add(new ChannelCongestionMetric
            {
                Channel = cg.Key.Channel,
                Band = cg.Key.BandGroup,
                AccessPointCount = cg.Count(),
                IsNonStandard24Ghz = cg.Key.BandGroup == "2.4 GHz" && !Standard24GhzChannels.Contains(cg.Key.Channel)
            });
        }

        // 4. Overall Posture Calculation
        int crit = findings.Count(f => f.Severity == FindingSeverity.Critical);
        int high = findings.Count(f => f.Severity == FindingSeverity.High);
        int med = findings.Count(f => f.Severity == FindingSeverity.Medium);
        int low = findings.Count(f => f.Severity == FindingSeverity.Low);
        int info = findings.Count(f => f.Severity == FindingSeverity.Informational);

        SecurityPostureLevel posture;
        string summary;

        if (crit > 0)
        {
            posture = SecurityPostureLevel.CriticalRisk;
            summary = $"{crit} critical risk finding(s) detected (e.g. WEP or severe vulnerability). Immediate remediation required.";
        }
        else if (high > 0)
        {
            posture = SecurityPostureLevel.Vulnerable;
            summary = $"{high} high-severity finding(s) detected (e.g. unencrypted open networks, deprecated WPA1, or rogue AP indicators).";
        }
        else if (med > 0)
        {
            posture = SecurityPostureLevel.NeedsAttention;
            summary = $"{med} medium-severity finding(s) identified (e.g. WPS enabled). Defensive hardening recommended.";
        }
        else if (low > 0)
        {
            posture = SecurityPostureLevel.Satisfactory;
            summary = "Baseline wireless security observed (standard WPA2/WPA3). Consider router-side verification for PMF enforcement.";
        }
        else
        {
            posture = SecurityPostureLevel.Secure;
            summary = "All observed access points utilize modern WPA3-Personal or enterprise security configurations.";
        }

        return new SecurityAssessmentReport
        {
            ScannedAtUtc = DateTime.UtcNow,
            TotalAccessPoints = apList.Count,
            TotalOpenNetworks = openCount,
            TotalWpa3Networks = wpa3Count,
            CriticalFindingsCount = crit,
            HighFindingsCount = high,
            MediumFindingsCount = med,
            LowFindingsCount = low,
            InfoFindingsCount = info,
            OverallPosture = posture,
            PostureSummary = summary,
            AccessPoints = apList,
            Findings = findings.OrderByDescending(f => f.Severity).ToList(),
            DuplicateSsidAnalyses = duplicateAnalyses,
            ChannelCongestion = congestionMetrics
        };
    }
}
