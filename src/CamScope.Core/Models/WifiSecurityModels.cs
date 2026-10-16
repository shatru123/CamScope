using System.Text.Json.Serialization;
using CamScope.Core.Enums;

namespace CamScope.Core.Models;

public sealed class WifiAccessPointObservation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Ssid { get; set; } = string.Empty;
    public string? Bssid { get; set; }
    public int? Rssi { get; set; }
    public int Channel { get; set; }
    public string? Band { get; set; } = "5 GHz";
    public int? FrequencyMhz { get; set; }
    public string? PhyMode { get; set; }
    public WifiSecurityStandard SecurityStandard { get; set; } = WifiSecurityStandard.Unknown;
    public string RawSecurityString { get; set; } = "Unknown";
    public bool? SupportsWps { get; set; }
    public PmfStatus PmfCapability { get; set; } = PmfStatus.UnknownNotObservable;
    public bool IsHidden { get; set; }
    public bool IsCurrent { get; set; }
    public int? BeaconInterval { get; set; }

    public string DisplaySsid => IsHidden || string.IsNullOrWhiteSpace(Ssid)
        ? "<Hidden Network>"
        : Ssid;

    public string DisplayBssid => !string.IsNullOrWhiteSpace(Bssid)
        ? Bssid
        : "Unknown / Not Observable";

    public bool HasSignalStrength => Rssi.HasValue;

    public int SignalQualityPercent => Rssi.HasValue
        ? Math.Clamp((int)((Rssi.Value + 100) * 2), 0, 100)
        : 0;

    public string SignalText => Rssi.HasValue
        ? $"{Rssi.Value} dBm ({SignalQualityPercent}%)"
        : "Unknown (Not reported)";

    public bool IsSecured => SecurityStandard != WifiSecurityStandard.Open;

    public string BandGroup => (Band ?? string.Empty).Contains('6')
        ? "6 GHz"
        : (Band ?? string.Empty).Contains('5')
            ? "5 GHz"
            : "2.4 GHz";
}

public sealed class SecurityFinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? AccessPointId { get; set; }
    public string Ssid { get; set; } = string.Empty;
    public string? Bssid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "Wireless Security";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Informational;
    public FindingConfidence Confidence { get; set; } = FindingConfidence.Confirmed;
    public string EvidenceObserved { get; set; } = string.Empty;
    public string ImpactExplanation { get; set; } = string.Empty;
    public string RecommendedRemediation { get; set; } = string.Empty;
    public bool RequiresRouterSideVerification { get; set; }

    public string SeverityBadgeColor => Severity switch
    {
        FindingSeverity.Critical => "#EF4444",      // Red
        FindingSeverity.High => "#F97316",          // Orange
        FindingSeverity.Medium => "#F59E0B",        // Amber
        FindingSeverity.Low => "#3B82F6",           // Blue
        _ => "#10B981"                              // Green/Info
    };

    public string ConfidenceText => Confidence switch
    {
        FindingConfidence.Confirmed => "Confirmed Observable",
        FindingConfidence.Inferred => "Inferred Pattern",
        FindingConfidence.ObservableAdvertisementOnly => "Beacon Advertisement Only",
        FindingConfidence.UnobservableRouterSideRequired => "Requires Router Verification",
        _ => "Unknown"
    };
}

public sealed class DuplicateSsidAnalysis
{
    public string Ssid { get; set; } = string.Empty;
    public int AccessPointsCount { get; set; }
    public IReadOnlyList<string> DistinctBssids { get; set; } = [];
    public bool HasSecurityMismatch { get; set; }
    public bool HasRogueRisk { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class ChannelCongestionMetric
{
    public int Channel { get; set; }
    public string Band { get; set; } = "2.4 GHz";
    public int AccessPointCount { get; set; }
    public bool IsNonStandard24Ghz { get; set; }
}

public sealed class SecurityAssessmentReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
    public string? InterfaceName { get; set; }
    public SecurityPostureLevel OverallPosture { get; set; } = SecurityPostureLevel.Satisfactory;
    public string PostureSummary { get; set; } = string.Empty;
    public int TotalAccessPoints { get; set; }
    public int TotalOpenNetworks { get; set; }
    public int TotalWpa3Networks { get; set; }
    public int CriticalFindingsCount { get; set; }
    public int HighFindingsCount { get; set; }
    public int MediumFindingsCount { get; set; }
    public int LowFindingsCount { get; set; }
    public int InfoFindingsCount { get; set; }

    public IReadOnlyList<WifiAccessPointObservation> AccessPoints { get; set; } = [];
    public IReadOnlyList<SecurityFinding> Findings { get; set; } = [];
    public IReadOnlyList<DuplicateSsidAnalysis> DuplicateSsidAnalyses { get; set; } = [];
    public IReadOnlyList<ChannelCongestionMetric> ChannelCongestion { get; set; } = [];

    public string PostureBadgeColor => OverallPosture switch
    {
        SecurityPostureLevel.CriticalRisk => "#EF4444",
        SecurityPostureLevel.Vulnerable => "#F97316",
        SecurityPostureLevel.NeedsAttention => "#F59E0B",
        SecurityPostureLevel.Satisfactory => "#06B6D4",
        _ => "#10B981"
    };

    public string PostureBadgeText => OverallPosture switch
    {
        SecurityPostureLevel.CriticalRisk => "CRITICAL RISK",
        SecurityPostureLevel.Vulnerable => "VULNERABLE",
        SecurityPostureLevel.NeedsAttention => "NEEDS ATTENTION",
        SecurityPostureLevel.Satisfactory => "SATISFACTORY",
        _ => "SECURE"
    };
}

public sealed class AuditChecklistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string VerificationGuide { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public string? OperatorNotes { get; set; }
}

public sealed class AuthorizedAuditRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Ssid { get; set; } = string.Empty;
    public string? Bssid { get; set; }
    public DateTime AuditedAtUtc { get; set; } = DateTime.UtcNow;
    public bool OwnershipConfirmed { get; set; }
    public List<AuditChecklistItem> Items { get; set; } = [];
    public string? OperatorNotes { get; set; }
    public bool RouterVerified { get; set; }
}

public sealed class PasswordStrengthEstimate
{
    public int Score { get; set; } // 0 to 4
    public double EntropyBits { get; set; }
    public string Rating { get; set; } = "Weak";
    public string BadgeColor { get; set; } = "#EF4444";
    public string EstimatedCrackTimeOfflineGpu { get; set; } = "< 1 second";
    public string EstimatedCrackTimeOfflineCpu { get; set; } = "< 1 second";
    public IReadOnlyList<string> Suggestions { get; set; } = [];
}

public sealed class WifiScanSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
    public int TotalNetworksFound { get; set; }
    public SecurityPostureLevel OverallPosture { get; set; } = SecurityPostureLevel.Satisfactory;
    public string ObservationsJson { get; set; } = "[]";
    public string FindingsSummaryJson { get; set; } = "[]";
}

public sealed class WifiScanSessionResult
{
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public bool PlatformSupported { get; set; } = true;
    public bool IsSimulated { get; set; }
    public IReadOnlyList<WifiAccessPointObservation> Observations { get; set; } = [];
    public DateTime ScannedAtUtc { get; set; } = DateTime.UtcNow;
}
