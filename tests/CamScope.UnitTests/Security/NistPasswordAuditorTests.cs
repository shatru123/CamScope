using System;
using System.Linq;
using CamScope.Application.Security;
using CamScope.Core.Models;
using Xunit;

namespace CamScope.UnitTests.Security;

public class NistPasswordAuditorTests
{
    private readonly NistPasswordAuditor _auditor = new();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AuditPassword_EmptyOrNull_ReturnsNonCompliant(string? password)
    {
        var result = _auditor.AuditPassword(password ?? string.Empty);

        Assert.False(result.IsCompliant);
        Assert.Equal(NistComplianceTier.NonCompliant, result.ComplianceTier);
        Assert.Equal(0, result.PasswordLength);
        Assert.Equal(0.0, result.EntropyBits);
        Assert.Contains(result.Rules, r => r.RuleId == "LENGTH" && !r.Passed);
    }

    [Theory]
    [InlineData("short", false)]       // 5 chars < 8
    [InlineData("1234567", false)]     // 7 chars < 8
    public void AuditPassword_StandardUser_BelowMinLength_FailsLengthRule(string password, bool isAdmin)
    {
        var result = _auditor.AuditPassword(password, isPrivilegedAdmin: isAdmin);

        Assert.False(result.IsCompliant);
        var lengthRule = result.Rules.FirstOrDefault(r => r.RuleId == "LENGTH");
        Assert.NotNull(lengthRule);
        Assert.False(lengthRule.Passed);
        Assert.Equal(NistRuleSeverity.Critical, lengthRule.Severity);
    }

    [Fact]
    public void AuditPassword_PrivilegedAdmin_Below14Chars_FailsLengthRule()
    {
        // 13 characters: valid for standard user, but insufficient for privileged admin
        string candidate = "Solaris-Delta";
        Assert.Equal(13, candidate.Length);

        var userResult = _auditor.AuditPassword(candidate, isPrivilegedAdmin: false);
        var adminResult = _auditor.AuditPassword(candidate, isPrivilegedAdmin: true);

        Assert.True(userResult.Rules.First(r => r.RuleId == "LENGTH").Passed);
        Assert.False(adminResult.Rules.First(r => r.RuleId == "LENGTH").Passed);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("12345678")]
    [InlineData("admin1234")]
    [InlineData("hikvision")]
    [InlineData("dahua123")]
    [InlineData("cctvcamera")]
    public void AuditPassword_BlocklistedAndCameraDefaults_FailsBlocklistRule(string weakSecret)
    {
        var result = _auditor.AuditPassword(weakSecret);

        Assert.False(result.IsCompliant);
        var blocklistRule = result.Rules.FirstOrDefault(r => r.RuleId == "BLOCKLIST");
        Assert.NotNull(blocklistRule);
        Assert.False(blocklistRule.Passed);
        Assert.Equal(NistRuleSeverity.Critical, blocklistRule.Severity);
    }

    [Theory]
    [InlineData("aaaa-Falcon-Ridge")]
    [InlineData("Beacon-1111-Summit")]
    public void AuditPassword_RepetitiveCharacters_FlagsRepetitionRule(string password)
    {
        var result = _auditor.AuditPassword(password);

        var repRule = result.Rules.FirstOrDefault(r => r.RuleId == "REPETITION");
        Assert.NotNull(repRule);
        Assert.False(repRule.Passed);
        Assert.Equal(NistRuleSeverity.Warning, repRule.Severity);
    }

    [Theory]
    [InlineData("abcdef-Horizon-Sky")]
    [InlineData("12345-Galaxy-Nebula")]
    [InlineData("qwerty-Citadel-Sol")]
    public void AuditPassword_SequentialPatterns_FlagsSequenceRule(string password)
    {
        var result = _auditor.AuditPassword(password);

        var seqRule = result.Rules.FirstOrDefault(r => r.RuleId == "SEQUENCE");
        Assert.NotNull(seqRule);
        Assert.False(seqRule.Passed);
        Assert.Equal(NistRuleSeverity.Warning, seqRule.Severity);
    }

    [Fact]
    public void AuditPassword_ContextUsernameOrEntityName_FlagsContextRule()
    {
        var result = _auditor.AuditPassword(
            "Operator-CamScope-Passphrase",
            isPrivilegedAdmin: false,
            contextUsername: "operator",
            contextEntityName: "CamScope");

        var contextRule = result.Rules.FirstOrDefault(r => r.RuleId == "CONTEXT_SPECIFIC");
        Assert.NotNull(contextRule);
        Assert.False(contextRule.Passed);
        Assert.Equal(NistRuleSeverity.Warning, contextRule.Severity);
    }

    [Fact]
    public void AuditPassword_StrongPassphrase_CompliesWithNist()
    {
        // 4-word passphrase with high entropy
        var result = _auditor.AuditPassword("Granite-Nebula-Timber-Solace", isPrivilegedAdmin: true);

        Assert.True(result.IsCompliant);
        Assert.Equal(NistComplianceTier.Compliant, result.ComplianceTier);
        Assert.True(result.EntropyBits >= 60.0);
        Assert.All(result.Rules.Where(r => r.Severity == NistRuleSeverity.Critical), r => Assert.True(r.Passed));
    }

    [Fact]
    public void GenerateCompliantPassphrase_GeneratesValidFormatAndLength()
    {
        string passphrase = _auditor.GenerateCompliantPassphrase(4, "-");

        Assert.NotNull(passphrase);
        var parts = passphrase.Split('-');
        Assert.Equal(4, parts.Length);

        // Verify that the generated passphrase passes the auditor
        var audit = _auditor.AuditPassword(passphrase, isPrivilegedAdmin: true);
        Assert.True(audit.IsCompliant);
        Assert.True(audit.PasswordLength >= 14);
        Assert.True(audit.EntropyBits >= 50.0);
    }
}
