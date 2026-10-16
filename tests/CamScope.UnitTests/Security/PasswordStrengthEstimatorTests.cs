using CamScope.Application.Security;
using Xunit;

namespace CamScope.UnitTests.Security;

public class PasswordStrengthEstimatorTests
{
    private readonly PasswordStrengthEstimator _estimator = new();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Estimate_EmptyOrNull_ReturnsScoreZeroAndEmptyRating(string? password)
    {
        var result = _estimator.Estimate(password ?? string.Empty);

        Assert.Equal(0, result.Score);
        Assert.Equal("Empty", result.Rating);
        Assert.Equal(0.0, result.EntropyBits);
        Assert.NotEmpty(result.Suggestions);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("12345678")]
    [InlineData("admin123")]
    [InlineData("qwertyuiop")]
    public void Estimate_CommonDictionaryPassword_AppliesHighPenalty(string weakPassword)
    {
        var result = _estimator.Estimate(weakPassword);

        Assert.True(result.Score <= 1, $"Expected score <= 1 for '{weakPassword}', got {result.Score}");
        Assert.True(result.EntropyBits < 35.0, $"Expected low entropy for '{weakPassword}', got {result.EntropyBits}");
        Assert.Contains("second", result.EstimatedCrackTimeOfflineGpu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Suggestions, s => s.Contains("dictionary", StringComparison.OrdinalIgnoreCase) || s.Contains("common", StringComparison.OrdinalIgnoreCase) || s.Contains("pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Estimate_ModeratePassword_ReturnsFairOrGoodRating()
    {
        var result = _estimator.Estimate("Summer2024!Net");

        Assert.True(result.Score >= 2, $"Expected score >= 2, got {result.Score}");
        Assert.True(result.EntropyBits > 40.0, $"Expected entropy > 40, got {result.EntropyBits}");
    }

    [Fact]
    public void Estimate_StrongPassphrase_ReturnsVeryStrongScoreAndLongCrackTime()
    {
        var result = _estimator.Estimate("Correct-Horse-Battery-Staple-2026!#");

        Assert.Equal(4, result.Score);
        Assert.Equal("Very Strong", result.Rating);
        Assert.True(result.EntropyBits > 75.0, $"Expected entropy > 75 bits, got {result.EntropyBits}");
        Assert.True(result.EstimatedCrackTimeOfflineGpu.Contains("Centuries", StringComparison.OrdinalIgnoreCase) ||
                    result.EstimatedCrackTimeOfflineGpu.Contains("years", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.Suggestions);
    }

    [Fact]
    public void Estimate_RepeatedCharacters_PenalizesEntropy()
    {
        var repeating = _estimator.Estimate("aaaaaaaaaaaaaaaa");
        var varied = _estimator.Estimate("aB3$kL9#mP1!zQ8*");

        Assert.True(varied.EntropyBits > repeating.EntropyBits * 2,
            $"Varied entropy ({varied.EntropyBits}) should be much higher than repeating entropy ({repeating.EntropyBits})");
    }
}
