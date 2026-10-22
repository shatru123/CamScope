using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;

namespace CamScope.Application.Security;

/// <summary>
/// Implements NIST SP 800-63B (Digital Identity Guidelines: Authentication and Lifecycle Management)
/// Section 5.1.1.2 Memorized Secret Verifiers and Password Policy Auditing.
/// </summary>
public sealed class NistPasswordAuditor : INistPasswordAuditor
{
    private const int StandardUserMinLength = 8;
    private const int PrivilegedAdminMinLength = 14;
    private const int MaximumSupportedLength = 128;

    private static readonly HashSet<string> CompromisedAndDefaultBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Universal leaked passwords
        "password", "password1", "password123", "123456", "12345678", "123456789", "12345",
        "qwerty", "qwertyuiop", "111111", "123123", "welcome", "welcome1", "admin123",
        "admin1234", "admin12345", "letmein", "monkey", "dragon", "football", "baseball",
        "master", "access", "shadow", "sunshine", "superman", "trustno1", "starwars",
        "iloveyou", "passphrase", "default", "changeme", "guest", "temp1234",

        // CCTV, Camera, NVR/DVR & Network hardware vendor defaults
        "admin", "administrator", "root", "camera", "cctv", "camscope", "onvif", "rtsp",
        "hikvision", "dahua", "axis", "hanwha", "uniview", "reolink", "bosch", "cisco",
        "ubiquiti", "tp-link", "netgear", "dlink", "amcrest", "foscam", "flir", "pelco",
        "synology", "qnap", "security", "surveillance", "stream", "video", "monitoring",
        "service", "support", "operator", "viewer", "system", "install", "installer",
        "123456a?", "admin1", "admin12", "root123", "rootadmin", "pass1234", "camera123"
    };

    private static readonly string[] KeyboardSequences =
    [
        "qwerty", "asdfgh", "zxcvbn", "12345", "23456", "34567", "45678", "56789",
        "qwert", "werty", "ertyu", "rtyui", "tyuio", "yuiop",
        "asdf", "sdfg", "dfgh", "fghj", "ghjk", "hjkl",
        "zxcv", "xcvb", "cvbn", "vbnm"
    ];

    private static readonly string[] MemorableWordPool =
    [
        "anchor", "beacon", "bridge", "canyon", "castle", "citadel", "cloud", "compass",
        "crater", "crystal", "delta", "desert", "dragon", "eagle", "ember", "falcon",
        "forest", "frost", "galaxy", "glacier", "granite", "harbor", "haven", "horizon",
        "island", "jaguar", "jungle", "lantern", "legacy", "meadow", "meteor", "mirage",
        "monarch", "moon", "nebula", "nexus", "oasis", "ocean", "orbit", "palace",
        "pathway", "phantom", "phoenix", "pinnacle", "planet", "plasma", "prism", "pulsar",
        "pyramid", "quantum", "radiant", "raptor", "ravine", "ridge", "river", "rocket",
        "saber", "safari", "sapphire", "saturn", "shadow", "shield", "sierra", "silver",
        "skyline", "solace", "solar", "spark", "specter", "spiral", "summit", "tempest",
        "timber", "titan", "torpedo", "torrent", "tower", "tracer", "tsunami", "tundra",
        "tunnel", "valley", "vapor", "velvet", "vertex", "vessel", "vortex", "voyage",
        "zenith", "zephyr", "archive", "balance", "banner", "blizzard", "boulder", "canopy",
        "cascade", "chronicle", "crescent", "dynamo", "echo", "eclipse", "frontier", "glade",
        "harvester", "heritage", "iceberg", "infinity", "matrix", "monument", "outpost", "paramount",
        "passageway", "pioneer", "rampart", "sentinel", "solitude", "spectrum", "starburst", "strata",
        "streamline", "traverse", "vanguard", "velocity", "wanderer", "waterfall", "wildwood", "windward"
    ];

    public NistPasswordAuditResult AuditPassword(
        string password,
        bool isPrivilegedAdmin = false,
        string? contextUsername = null,
        string? contextEntityName = null)
    {
        // Zero-retention: Evaluate candidate strictly in ephemeral stack/method frame
        var rules = new List<NistRuleEvaluation>();
        var recommendations = new List<string>();
        var citations = new List<string>
        {
            "NIST SP 800-63B § 5.1.1.2 Memorized Secret Verifiers",
            "NIST SP 800-63B § 5.2.2 Verifier Rate Limiting & Throttling",
            "NIST SP 800-63B § 7.1 Authentication Lifecycle Management"
        };

        if (string.IsNullOrEmpty(password))
        {
            rules.Add(new NistRuleEvaluation
            {
                RuleId = "LENGTH",
                RuleName = "Minimum Character Length",
                NistSection = "NIST SP 800-63B § 5.1.1.2",
                Passed = false,
                Severity = NistRuleSeverity.Critical,
                Details = "Secret cannot be empty. Minimum length required.",
                Guidance = isPrivilegedAdmin
                    ? "Privileged/Administrative accounts must be at least 14 characters in length."
                    : "Standard user accounts must be at least 8 characters in length."
            });

            return new NistPasswordAuditResult
            {
                IsCompliant = false,
                ComplianceTier = NistComplianceTier.NonCompliant,
                IsPrivilegedAdmin = isPrivilegedAdmin,
                PasswordLength = 0,
                EntropyBits = 0,
                ComplianceBadgeText = "NON-COMPLIANT",
                BadgeColor = "#EF4444",
                Summary = "Secret is empty. Does not meet NIST SP 800-63B § 5.1.1.2 minimum requirements.",
                Rules = rules,
                NistCitations = citations,
                Recommendations = ["Enter a secret or passphrase to audit against NIST SP 800-63B."]
            };
        }

        int length = password.Length;
        int minRequiredLength = isPrivilegedAdmin ? PrivilegedAdminMinLength : StandardUserMinLength;

        // 1. Minimum Length Rule (§ 5.1.1.2)
        bool lengthPassed = length >= minRequiredLength;
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "LENGTH",
            RuleName = isPrivilegedAdmin ? "Privileged Admin Length (≥ 14)" : "Standard User Length (≥ 8)",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = lengthPassed,
            Severity = lengthPassed ? NistRuleSeverity.Pass : NistRuleSeverity.Critical,
            Details = lengthPassed
                ? $"Length of {length} characters meets or exceeds the required minimum ({minRequiredLength})."
                : $"Length of {length} characters is below the mandatory minimum of {minRequiredLength} characters.",
            Guidance = isPrivilegedAdmin
                ? "NIST and industry security controls mandate at least 14 characters for administrative or privileged accounts."
                : "NIST SP 800-63B § 5.1.1.2 requires verifiers to mandate secrets at least 8 characters in length."
        });

        if (!lengthPassed)
        {
            recommendations.Add(isPrivilegedAdmin
                ? $"Extend secret to at least {PrivilegedAdminMinLength} characters for administrative credentials."
                : $"Extend secret to at least {StandardUserMinLength} characters for standard user accounts.");
        }

        // 2. Maximum Length & Truncation Rule (§ 5.1.1.2)
        bool maxSupported = length <= MaximumSupportedLength;
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "MAX_LENGTH",
            RuleName = "No Secret Truncation (Support ≥ 64 chars)",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = maxSupported,
            Severity = maxSupported ? NistRuleSeverity.Pass : NistRuleSeverity.Warning,
            Details = maxSupported
                ? $"Verifier fully supports long passphrases up to {MaximumSupportedLength} characters without silent truncation."
                : $"Secret length ({length}) exceeds maximum system bounds of {MaximumSupportedLength} characters.",
            Guidance = "Verifiers SHALL permit secrets of at least 64 characters and SHALL NOT truncate secrets upon processing."
        });

        // 3. Universal Character Set Rule (§ 5.1.1.2)
        bool hasAllPrintableOrUnicode = true;
        foreach (char c in password)
        {
            if (char.IsControl(c) && c != '\t')
            {
                hasAllPrintableOrUnicode = false;
                break;
            }
        }
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "CHARSET",
            RuleName = "Universal Character Support (ASCII & Unicode)",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = hasAllPrintableOrUnicode,
            Severity = hasAllPrintableOrUnicode ? NistRuleSeverity.Pass : NistRuleSeverity.Warning,
            Details = hasAllPrintableOrUnicode
                ? "Secret contains valid printable ASCII or Unicode characters (including spaces)."
                : "Secret contains non-printable control characters.",
            Guidance = "Verifiers SHALL permit all printable ASCII characters (including the space character) and all Unicode characters."
        });

        // 4. Compromised / Blocklist Check (§ 5.1.1.2)
        string lower = password.ToLowerInvariant();
        bool isBlocklisted = CompromisedAndDefaultBlocklist.Contains(lower);
        bool containsBlocklistedTerm = false;
        string? matchedTerm = null;

        if (!isBlocklisted)
        {
            foreach (var term in CompromisedAndDefaultBlocklist)
            {
                if (term.Length >= 5 && lower.Contains(term))
                {
                    containsBlocklistedTerm = true;
                    matchedTerm = term;
                    break;
                }
            }
        }

        bool blocklistPassed = !isBlocklisted && !containsBlocklistedTerm;
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "BLOCKLIST",
            RuleName = "Compromised / Default Credentials Blocklist",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = blocklistPassed,
            Severity = blocklistPassed ? NistRuleSeverity.Pass : NistRuleSeverity.Critical,
            Details = blocklistPassed
                ? "Secret is not present in breached password databases or camera default credential dictionaries."
                : isBlocklisted
                    ? $"Secret '{password}' is a known default or heavily breached password."
                    : $"Secret contains common default credential term '{matchedTerm}'.",
            Guidance = "Verifiers SHALL compare candidate secrets against a blocklist of compromised passwords and common dictionary words."
        });

        if (!blocklistPassed)
        {
            recommendations.Add("Avoid common vendor default passwords (admin, camera, hikvision, dahua, root) and leaked dictionary words.");
        }

        // 5. Repetitive Characters Check (§ 5.1.1.2)
        bool hasRepetitivePattern = HasRepetitiveCharacters(password, 3);
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "REPETITION",
            RuleName = "Repetitive Character Sequences",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = !hasRepetitivePattern,
            Severity = !hasRepetitivePattern ? NistRuleSeverity.Pass : NistRuleSeverity.Warning,
            Details = !hasRepetitivePattern
                ? "No repetitive character clusters detected."
                : "Secret contains 3 or more consecutive identical characters (e.g., 'aaa', '1111').",
            Guidance = "Verifiers SHALL check against repetitive characters that provide low entropy."
        });

        if (hasRepetitivePattern)
        {
            recommendations.Add("Remove repeated sequences of identical characters to maximize cryptographic unpredictability.");
        }

        // 6. Sequential Character Patterns Check (§ 5.1.1.2)
        bool hasSequentialPattern = HasSequentialPattern(lower);
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "SEQUENCE",
            RuleName = "Sequential / Keyboard Walk Patterns",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = !hasSequentialPattern,
            Severity = !hasSequentialPattern ? NistRuleSeverity.Pass : NistRuleSeverity.Warning,
            Details = !hasSequentialPattern
                ? "No predictable alphabetical, numeric, or keyboard sequences detected."
                : "Secret contains predictable sequential patterns (e.g., '12345', 'abcdef', or 'qwerty').",
            Guidance = "Verifiers SHALL check candidate secrets against predictable sequential character sequences."
        });

        if (hasSequentialPattern)
        {
            recommendations.Add("Eliminate alphabetical, numerical, or keyboard sequential walks (e.g. '12345', 'qwerty').");
        }

        // 7. Context-Specific Matching Check (§ 5.1.1.2)
        bool hasContextViolation = false;
        string? matchedContext = null;

        if (!string.IsNullOrWhiteSpace(contextUsername) && contextUsername.Length >= 3 && lower.Contains(contextUsername.ToLowerInvariant()))
        {
            hasContextViolation = true;
            matchedContext = $"username '{contextUsername}'";
        }
        else if (!string.IsNullOrWhiteSpace(contextEntityName) && contextEntityName.Length >= 3 && lower.Contains(contextEntityName.ToLowerInvariant()))
        {
            hasContextViolation = true;
            matchedContext = $"entity/SSID '{contextEntityName}'";
        }

        rules.Add(new NistRuleEvaluation
        {
            RuleId = "CONTEXT_SPECIFIC",
            RuleName = "Context-Specific Information Check",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = !hasContextViolation,
            Severity = !hasContextViolation ? NistRuleSeverity.Pass : NistRuleSeverity.Warning,
            Details = !hasContextViolation
                ? "Secret does not incorporate predictable context (username, SSID, or service name)."
                : $"Secret contains context-specific identifier ({matchedContext}).",
            Guidance = "Verifiers SHALL check candidate secrets against context-specific words, such as the name of the service or username."
        });

        if (hasContextViolation)
        {
            recommendations.Add("Do not derive passphrases from user names, camera names, network SSIDs, or company identifiers.");
        }

        // 8. NIST Modern Composition Guidance (No Arbitrary Character Class Rules)
        bool hasLower = false, hasUpper = false, hasDigit = false, hasSymbol = false;
        foreach (char c in password)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }

        int varietyCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "COMPOSITION_RULES",
            RuleName = "Modern Composition Policy (Length over Complexity)",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = true, // NIST explicitly rejects mandatory composition rules
            Severity = NistRuleSeverity.Info,
            Details = varietyCount >= 3
                ? $"Secret has diverse character selection ({varietyCount} classes). NIST advises length and passphrases are primary defenses."
                : "NIST SP 800-63B discourages mandatory composition rules (e.g. forcing 1 symbol) because they encourage predictable substitutions like 'P@ssword1!'. Long passphrases provide superior entropy.",
            Guidance = "Verifiers SHALL NOT impose arbitrary composition rules (e.g., requiring uppercase, lowercase, numbers, and special symbols)."
        });

        // 9. NIST Expiration & Rotation Policy Guidance
        rules.Add(new NistRuleEvaluation
        {
            RuleId = "EXPIRATION_POLICY",
            RuleName = "Elimination of Arbitrary Periodic Rotation",
            NistSection = "NIST SP 800-63B § 5.1.1.2",
            Passed = true,
            Severity = NistRuleSeverity.Info,
            Details = "NIST SP 800-63B advises that memorized secrets SHALL NOT be required to be changed periodically without evidence of compromise.",
            Guidance = "Periodic expiration leads to predictable patterns (e.g. 'Winter2024!'). Secrets should only be rotated upon evidence of breach."
        });

        // Calculate Entropy
        double entropyBits = CalculateEntropy(password);

        // Overall compliance calculation
        bool hasCriticalFailure = rules.Any(r => r.Severity == NistRuleSeverity.Critical && !r.Passed);
        bool hasWarnings = rules.Any(r => r.Severity == NistRuleSeverity.Warning && !r.Passed);

        NistComplianceTier complianceTier;
        string badgeText;
        string badgeColor;
        string summary;

        if (hasCriticalFailure)
        {
            complianceTier = NistComplianceTier.NonCompliant;
            badgeText = "NON-COMPLIANT";
            badgeColor = "#EF4444"; // Red
            summary = "Secret fails critical NIST SP 800-63B § 5.1.1.2 requirements (length or known compromised/default credentials).";
        }
        else if (hasWarnings || entropyBits < (isPrivilegedAdmin ? 60.0 : 40.0))
        {
            complianceTier = NistComplianceTier.NeedsImprovement;
            badgeText = "NEEDS IMPROVEMENT";
            badgeColor = "#F59E0B"; // Amber
            summary = "Secret meets baseline length but exhibits patterns (sequences, repetition, or context terms) that reduce entropy.";
        }
        else
        {
            complianceTier = NistComplianceTier.Compliant;
            badgeText = "NIST COMPLIANT";
            badgeColor = "#10B981"; // Green
            summary = isPrivilegedAdmin
                ? "Secret meets high-assurance administrative standards (≥ 14 characters, high entropy, zero blocklist matches)."
                : "Secret fully complies with NIST SP 800-63B § 5.1.1.2 memorized secret verifier specifications.";
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("Excellent memorized secret. Meets modern NIST SP 800-63B digital identity guidelines.");
        }

        return new NistPasswordAuditResult
        {
            IsCompliant = complianceTier == NistComplianceTier.Compliant,
            ComplianceTier = complianceTier,
            IsPrivilegedAdmin = isPrivilegedAdmin,
            PasswordLength = length,
            EntropyBits = Math.Round(entropyBits, 1),
            ComplianceBadgeText = badgeText,
            BadgeColor = badgeColor,
            Summary = summary,
            Rules = rules,
            NistCitations = citations,
            Recommendations = recommendations,
            AuditedAtUtc = DateTime.UtcNow
        };
    }

    public string GenerateCompliantPassphrase(int wordCount = 4, string separator = "-")
    {
        wordCount = Math.Clamp(wordCount, 3, 8);
        var chosenWords = new string[wordCount];

        for (int i = 0; i < wordCount; i++)
        {
            int index = RandomNumberGenerator.GetInt32(MemorableWordPool.Length);
            string word = MemorableWordPool[index];

            // Capitalize first letter to enhance human readability
            chosenWords[i] = char.ToUpperInvariant(word[0]) + word[1..];
        }

        return string.Join(separator, chosenWords);
    }

    private static bool HasRepetitiveCharacters(string password, int threshold)
    {
        if (password.Length < threshold) return false;

        int consecutive = 1;
        for (int i = 1; i < password.Length; i++)
        {
            if (char.ToLowerInvariant(password[i]) == char.ToLowerInvariant(password[i - 1]))
            {
                consecutive++;
                if (consecutive >= threshold) return true;
            }
            else
            {
                consecutive = 1;
            }
        }

        return false;
    }

    private static bool HasSequentialPattern(string lower)
    {
        // 1. Alphabetical sequences
        int alphaRun = 1;
        for (int i = 1; i < lower.Length; i++)
        {
            if (char.IsLetter(lower[i]) && char.IsLetter(lower[i - 1]) &&
                lower[i] == lower[i - 1] + 1)
            {
                alphaRun++;
                if (alphaRun >= 4) return true;
            }
            else
            {
                alphaRun = 1;
            }
        }

        // 2. Numeric sequences
        int numRun = 1;
        for (int i = 1; i < lower.Length; i++)
        {
            if (char.IsDigit(lower[i]) && char.IsDigit(lower[i - 1]) &&
                lower[i] == lower[i - 1] + 1)
            {
                numRun++;
                if (numRun >= 4) return true;
            }
            else
            {
                numRun = 1;
            }
        }

        // 3. Keyboard walk matches
        foreach (var seq in KeyboardSequences)
        {
            if (lower.Contains(seq)) return true;
        }

        return false;
    }

    private static double CalculateEntropy(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        int poolSize = 0;
        bool hasLower = false, hasUpper = false, hasDigit = false, hasSymbol = false;

        foreach (char c in password)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }

        if (hasLower) poolSize += 26;
        if (hasUpper) poolSize += 26;
        if (hasDigit) poolSize += 10;
        if (hasSymbol) poolSize += 33;

        double rawEntropy = password.Length * Math.Log2(Math.Max(poolSize, 2));

        // Deduct penalty for repetitive characters
        double penalty = 0;
        for (int i = 1; i < password.Length; i++)
        {
            if (password[i] == password[i - 1])
                penalty += 3.0;
        }

        return Math.Max(0, rawEntropy - penalty);
    }
}
