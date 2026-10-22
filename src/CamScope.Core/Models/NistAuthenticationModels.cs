using System;
using System.Collections.Generic;

namespace CamScope.Core.Models;

public enum NistComplianceTier
{
    Compliant,
    NeedsImprovement,
    NonCompliant
}

public enum NistRuleSeverity
{
    Pass,
    Info,
    Warning,
    Critical
}

public sealed class NistRuleEvaluation
{
    public string RuleId { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string NistSection { get; set; } = "NIST SP 800-63B § 5.1.1.2";
    public bool Passed { get; set; }
    public NistRuleSeverity Severity { get; set; } = NistRuleSeverity.Pass;
    public string Details { get; set; } = string.Empty;
    public string Guidance { get; set; } = string.Empty;

    public string BadgeColor => Severity switch
    {
        NistRuleSeverity.Pass => "#10B981",    // Green
        NistRuleSeverity.Info => "#3B82F6",    // Blue
        NistRuleSeverity.Warning => "#F59E0B", // Amber
        NistRuleSeverity.Critical => "#EF4444",// Red
        _ => "#6B7280"
    };

    public string StatusIcon => Passed ? "✓" : (Severity == NistRuleSeverity.Critical ? "✕" : "⚠");
}

public sealed class NistPasswordAuditResult
{
    public bool IsCompliant { get; set; }
    public NistComplianceTier ComplianceTier { get; set; } = NistComplianceTier.NonCompliant;
    public bool IsPrivilegedAdmin { get; set; }
    public int PasswordLength { get; set; }
    public double EntropyBits { get; set; }
    public string ComplianceBadgeText { get; set; } = "NON-COMPLIANT";
    public string BadgeColor { get; set; } = "#EF4444";
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<NistRuleEvaluation> Rules { get; set; } = [];
    public IReadOnlyList<string> NistCitations { get; set; } = [];
    public IReadOnlyList<string> Recommendations { get; set; } = [];
    public DateTime AuditedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AdminAuthAttemptResult
{
    public bool Success { get; set; }
    public bool IsLockedOut { get; set; }
    public int RemainingAttemptsBeforeLockout { get; set; }
    public int ThrottleDelayMs { get; set; }
    public DateTime? LockoutExpiresUtc { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ClientIp { get; set; } = "127.0.0.1";
    public DateTime AttemptedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AdminAccountState
{
    public string Username { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public DateTime? LastFailedAtUtc { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
    public bool IsLocked => LockoutUntilUtc.HasValue && LockoutUntilUtc.Value > DateTime.UtcNow;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public int Iterations { get; set; } = 600_000;
}
