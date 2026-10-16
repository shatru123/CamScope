using CamScope.Core.Interfaces;
using CamScope.Core.Models;

namespace CamScope.Application.Security;

public sealed class PasswordStrengthEstimator : IPasswordStrengthEstimator
{
    private static readonly string[] CommonPatterns =
    [
        "password", "admin", "123456", "qwerty", "camera", "cctv", "welcome",
        "default", "wireless", "security", "passphrase", "internet"
    ];

    public PasswordStrengthEstimate Estimate(string password)
    {
        // Zero-retention guarantee: password is evaluated purely within this method scope
        if (string.IsNullOrEmpty(password))
        {
            return new PasswordStrengthEstimate
            {
                Score = 0,
                EntropyBits = 0,
                Rating = "Empty",
                BadgeColor = "#EF4444",
                EstimatedCrackTimeOfflineGpu = "Instant (< 1 ms)",
                EstimatedCrackTimeOfflineCpu = "Instant (< 1 ms)",
                Suggestions = ["Enter a candidate passphrase to evaluate its defensive cryptographic strength."]
            };
        }

        int length = password.Length;
        int poolSize = 0;
        bool hasLower = false;
        bool hasUpper = false;
        bool hasDigit = false;
        bool hasSymbol = false;

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

        double rawEntropy = length * Math.Log2(Math.Max(poolSize, 2));

        // Pattern and sequence penalties
        double penalty = 0;
        string lower = password.ToLowerInvariant();

        foreach (var pattern in CommonPatterns)
        {
            if (lower.Contains(pattern))
            {
                penalty += 15;
            }
        }

        // Sequential repetition penalty
        int repetitionCount = 0;
        for (int i = 1; i < length; i++)
        {
            if (password[i] == password[i - 1])
                repetitionCount++;
        }
        penalty += repetitionCount * 3;

        double effectiveEntropy = Math.Max(0, rawEntropy - penalty);

        // Score 0 to 4
        int score;
        string rating;
        string badgeColor;

        if (length < 8 || effectiveEntropy < 30)
        {
            score = 0;
            rating = "Very Weak";
            badgeColor = "#EF4444"; // Red
        }
        else if (length < 12 || effectiveEntropy < 50)
        {
            score = 1;
            rating = "Weak";
            badgeColor = "#F97316"; // Orange
        }
        else if (effectiveEntropy < 70)
        {
            score = 2;
            rating = "Fair";
            badgeColor = "#F59E0B"; // Amber
        }
        else if (effectiveEntropy < 90)
        {
            score = 3;
            rating = "Strong";
            badgeColor = "#3B82F6"; // Blue
        }
        else
        {
            score = 4;
            rating = "Very Strong";
            badgeColor = "#10B981"; // Green
        }

        // Compute offline crack times (WPA2 PBKDF2: 100k H/s on CPU, 100M H/s on high-end GPU cluster)
        string gpuTime = FormatCrackTime(effectiveEntropy, 100_000_000.0);
        string cpuTime = FormatCrackTime(effectiveEntropy, 100_000.0);

        var suggestions = new List<string>();
        if (length < 16) suggestions.Add("Increase length to at least 16 characters or use a multi-word passphrase.");
        if (!hasUpper) suggestions.Add("Include uppercase letters (A-Z).");
        if (!hasDigit) suggestions.Add("Include numeric characters (0-9).");
        if (!hasSymbol) suggestions.Add("Include special symbols (!, @, #, $, etc.).");
        if (penalty > 0) suggestions.Add("Avoid common words, sequential characters, and repeated letters.");
        if (suggestions.Count == 0) suggestions.Add("Excellent passphrase. High entropy provides strong defense against offline attacks.");

        return new PasswordStrengthEstimate
        {
            Score = score,
            EntropyBits = Math.Round(effectiveEntropy, 1),
            Rating = rating,
            BadgeColor = badgeColor,
            EstimatedCrackTimeOfflineGpu = gpuTime,
            EstimatedCrackTimeOfflineCpu = cpuTime,
            Suggestions = suggestions
        };
    }

    private static string FormatCrackTime(double entropyBits, double hashesPerSecond)
    {
        if (entropyBits <= 0) return "< 1 second";

        // Expected attempts to crack (50% probability): 2^(entropy - 1)
        double seconds = Math.Pow(2.0, Math.Min(entropyBits - 1, 128)) / hashesPerSecond;

        if (seconds < 1.0) return "< 1 second";
        if (seconds < 60.0) return $"{seconds:F0} seconds";
        if (seconds < 3600.0) return $"{seconds / 60.0:F1} minutes";
        if (seconds < 86400.0) return $"{seconds / 3600.0:F1} hours";
        if (seconds < 86400.0 * 365.25) return $"{seconds / 86400.0:F0} days";
        if (seconds < 86400.0 * 365.25 * 100) return $"{seconds / (86400.0 * 365.25):F1} years";
        if (seconds < 86400.0 * 365.25 * 100_000) return $"{seconds / (86400.0 * 365.25):F0} years";

        return "Centuries (Computationally infeasible)";
    }
}
