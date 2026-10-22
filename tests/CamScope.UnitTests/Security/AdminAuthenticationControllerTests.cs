using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CamScope.Application.Security;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Security;

public class AdminAuthenticationControllerTests
{
    private sealed class MockAuditService : IAuditService
    {
        public List<ActivityAuditEntry> LoggedEntries { get; } = [];

        public Task LogAsync(
            AuditAction action,
            string? targetEndpoint,
            bool succeeded,
            string details,
            string? userOrSource = null,
            CancellationToken ct = default)
        {
            LoggedEntries.Add(new ActivityAuditEntry
            {
                Action = action,
                TargetEndpoint = targetEndpoint,
                Succeeded = succeeded,
                Details = details,
                UserOrSource = userOrSource,
                TimestampUtc = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int count = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>(LoggedEntries);

        public Task ClearLogsAsync(CancellationToken ct = default)
        {
            LoggedEntries.Clear();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AuthenticateAsync_ValidDefaultCredentials_SucceedsAndResetsFailures()
    {
        var audit = new MockAuditService();
        var controller = new AdminAuthenticationController(audit, NullLogger<AdminAuthenticationController>.Instance);

        var result = await controller.AuthenticateAsync("admin", "CamScope-Citadel-Sentinel-2026!");

        Assert.True(result.Success);
        Assert.False(result.IsLockedOut);
        Assert.Equal(5, result.RemainingAttemptsBeforeLockout);
        Assert.Contains(audit.LoggedEntries, e => e.Action == AuditAction.AdminAuthAttemptSucceeded);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidPassword_DecrementsRemainingAttempts()
    {
        var audit = new MockAuditService();
        var controller = new AdminAuthenticationController(audit, NullLogger<AdminAuthenticationController>.Instance);

        var result = await controller.AuthenticateAsync("admin", "WrongPassword123!");

        Assert.False(result.Success);
        Assert.False(result.IsLockedOut);
        Assert.Equal(4, result.RemainingAttemptsBeforeLockout);
        Assert.Contains(audit.LoggedEntries, e => e.Action == AuditAction.AdminAuthAttemptFailed);
    }

    [Fact]
    public async Task AuthenticateAsync_FiveFailures_TriggersNistLockout()
    {
        var audit = new MockAuditService();
        var controller = new AdminAuthenticationController(audit, NullLogger<AdminAuthenticationController>.Instance);

        AdminAuthAttemptResult? lastResult = null;
        for (int i = 0; i < 5; i++)
        {
            lastResult = await controller.AuthenticateAsync("admin", $"BadPassword_{i}");
        }

        Assert.NotNull(lastResult);
        Assert.False(lastResult.Success);
        Assert.True(lastResult.IsLockedOut);
        Assert.NotNull(lastResult.LockoutExpiresUtc);
        Assert.Contains(audit.LoggedEntries, e => e.Action == AuditAction.AdminAccountLocked);

        // Immediate subsequent attempt while locked out should be throttled
        var lockedAttempt = await controller.AuthenticateAsync("admin", "CamScope-Citadel-Sentinel-2026!");
        Assert.False(lockedAttempt.Success);
        Assert.True(lockedAttempt.IsLockedOut);
        Assert.Contains(audit.LoggedEntries, e => e.Action == AuditAction.AdminAuthThrottled);
    }

    [Fact]
    public async Task ResetLockoutAsync_ClearsLockoutStateAndRestoresAccess()
    {
        var audit = new MockAuditService();
        var controller = new AdminAuthenticationController(audit, NullLogger<AdminAuthenticationController>.Instance);

        // Trigger lockout
        for (int i = 0; i < 5; i++)
        {
            await controller.AuthenticateAsync("admin", "BadPassword");
        }

        var stateBefore = controller.GetAccountState("admin");
        Assert.True(stateBefore.IsLocked);

        // Admin resets lockout
        await controller.ResetLockoutAsync("admin");

        var stateAfter = controller.GetAccountState("admin");
        Assert.False(stateAfter.IsLocked);
        Assert.Equal(0, stateAfter.FailedAttempts);
        Assert.Contains(audit.LoggedEntries, e => e.Action == AuditAction.AdminAccountUnlocked);

        // Now authentication succeeds
        var result = await controller.AuthenticateAsync("admin", "CamScope-Citadel-Sentinel-2026!");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task SetPasswordAsync_UpdatesPasswordHashUsingPbkdf2()
    {
        var audit = new MockAuditService();
        var controller = new AdminAuthenticationController(audit, NullLogger<AdminAuthenticationController>.Instance);

        string newPassphrase = "Quantum-Glacier-Harbor-Zenith-2026#";
        await controller.SetPasswordAsync("admin", newPassphrase);

        var state = controller.GetAccountState("admin");
        Assert.Equal(600_000, state.Iterations);
        Assert.False(string.IsNullOrEmpty(state.Salt));
        Assert.False(string.IsNullOrEmpty(state.PasswordHash));

        // Old password fails
        var oldResult = await controller.AuthenticateAsync("admin", "CamScope-Citadel-Sentinel-2026!");
        Assert.False(oldResult.Success);

        // New password succeeds
        var newResult = await controller.AuthenticateAsync("admin", newPassphrase);
        Assert.True(newResult.Success);
    }
}
