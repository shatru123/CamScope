using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Application.Security;

/// <summary>
/// Implements NIST SP 800-63B Section 5.2.2 Verifier Requirements, Section 5.1.1.2 Memorized Secret Storage,
/// and Section 7.1 Authentication Lifecycle Management.
/// Enforces adaptive rate limiting, exponential backoff, constant-time verification, salted PBKDF2 hashing,
/// and tamper-evident audit logging.
/// </summary>
public sealed class AdminAuthenticationController : IAdminAuthenticationController
{
    private const int Iterations = 600_000; // NIST / OWASP recommended PBKDF2-HMAC-SHA256 work factor
    private const int SaltSizeBytes = 16;   // 128-bit cryptographically secure random salt
    private const int HashSizeBytes = 32;   // 256-bit derived key
    private const int MaxFailedAttemptsBeforeLockout = 5;
    private static readonly TimeSpan DefaultLockoutDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AdminAccountState> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _ipFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAuditService _auditService;
    private readonly ILogger<AdminAuthenticationController> _logger;

    public AdminAuthenticationController(
        IAuditService auditService,
        ILogger<AdminAuthenticationController> logger)
    {
        _auditService = auditService;
        _logger = logger;

        // Initialize default root/operator administrator with secure PBKDF2 hash
        InitializeDefaultAccount("admin", "CamScope-Citadel-Sentinel-2026!");
    }

    public async Task<AdminAuthAttemptResult> AuthenticateAsync(
        string username,
        string candidatePassword,
        string clientIp = "127.0.0.1",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var now = DateTime.UtcNow;

        if (!_accounts.TryGetValue(username, out var account))
        {
            // Dummy computation to equalize execution time and prevent username enumeration timing attacks
            ComputeHash("Dummy-Password-Protection", GenerateSalt());

            await _auditService.LogAsync(
                AuditAction.AdminAuthAttemptFailed,
                username,
                succeeded: false,
                $"Authentication failed: Invalid credentials for user '{username}' from {clientIp}.",
                userOrSource: clientIp,
                ct: ct);

            return new AdminAuthAttemptResult
            {
                Success = false,
                IsLockedOut = false,
                RemainingAttemptsBeforeLockout = 0,
                ThrottleDelayMs = 0,
                Message = "Invalid credentials.",
                ClientIp = clientIp,
                AttemptedAtUtc = now
            };
        }

        // Check if account is actively locked out per NIST SP 800-63B § 5.2.2
        if (account.IsLocked)
        {
            var remainingLockout = account.LockoutUntilUtc!.Value - now;
            var remainingSeconds = Math.Max(1, (int)remainingLockout.TotalSeconds);

            await _auditService.LogAsync(
                AuditAction.AdminAuthThrottled,
                username,
                succeeded: false,
                $"Authentication blocked: Account '{username}' locked out for {remainingSeconds}s. Request rejected.",
                userOrSource: clientIp,
                ct: ct);

            return new AdminAuthAttemptResult
            {
                Success = false,
                IsLockedOut = true,
                RemainingAttemptsBeforeLockout = 0,
                ThrottleDelayMs = remainingSeconds * 1000,
                LockoutExpiresUtc = account.LockoutUntilUtc,
                Message = $"Account locked due to consecutive failed attempts. Try again in {remainingSeconds} second(s).",
                ClientIp = clientIp,
                AttemptedAtUtc = now
            };
        }

        // Adaptive rate limiting throttle delay
        int throttleDelayMs = CalculateThrottleDelay(account.FailedAttempts);
        if (throttleDelayMs > 0)
        {
            _logger.LogInformation("Enforcing adaptive throttle delay of {DelayMs}ms for user {Username}", throttleDelayMs, username);
            await Task.Delay(throttleDelayMs, ct);
        }

        // Verify password hash in constant time
        byte[] salt = Convert.FromBase64String(account.Salt);
        byte[] expectedHash = Convert.FromBase64String(account.PasswordHash);
        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            candidatePassword,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        bool isValid = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);

        if (isValid)
        {
            // Reset failure counters upon successful authentication
            account.FailedAttempts = 0;
            account.LastFailedAtUtc = null;
            account.LockoutUntilUtc = null;
            _ipFailureCounts.TryRemove(clientIp, out _);

            await _auditService.LogAsync(
                AuditAction.AdminAuthAttemptSucceeded,
                username,
                succeeded: true,
                $"Administrative authentication successful for '{username}'. Session authenticated.",
                userOrSource: clientIp,
                ct: ct);

            return new AdminAuthAttemptResult
            {
                Success = true,
                IsLockedOut = false,
                RemainingAttemptsBeforeLockout = MaxFailedAttemptsBeforeLockout,
                ThrottleDelayMs = 0,
                Message = "Authentication successful. Access granted.",
                ClientIp = clientIp,
                AttemptedAtUtc = now
            };
        }

        // Authentication failed
        account.FailedAttempts++;
        account.LastFailedAtUtc = now;
        _ipFailureCounts.AddOrUpdate(clientIp, 1, (_, current) => current + 1);

        int remainingAttempts = Math.Max(0, MaxFailedAttemptsBeforeLockout - account.FailedAttempts);
        bool shouldLockout = account.FailedAttempts >= MaxFailedAttemptsBeforeLockout;

        if (shouldLockout)
        {
            account.LockoutUntilUtc = now.Add(DefaultLockoutDuration);

            await _auditService.LogAsync(
                AuditAction.AdminAccountLocked,
                username,
                succeeded: false,
                $"NIST SP 800-63B Lockout triggered: Account '{username}' locked for {DefaultLockoutDuration.TotalMinutes}m after {account.FailedAttempts} failed attempts.",
                userOrSource: clientIp,
                ct: ct);

            return new AdminAuthAttemptResult
            {
                Success = false,
                IsLockedOut = true,
                RemainingAttemptsBeforeLockout = 0,
                ThrottleDelayMs = (int)DefaultLockoutDuration.TotalMilliseconds,
                LockoutExpiresUtc = account.LockoutUntilUtc,
                Message = $"Maximum authentication attempts exceeded. Account locked for {DefaultLockoutDuration.TotalMinutes} minutes.",
                ClientIp = clientIp,
                AttemptedAtUtc = now
            };
        }

        await _auditService.LogAsync(
            AuditAction.AdminAuthAttemptFailed,
            username,
            succeeded: false,
            $"Administrative authentication failed for '{username}' (Attempt {account.FailedAttempts}/{MaxFailedAttemptsBeforeLockout}).",
            userOrSource: clientIp,
            ct: ct);

        int nextThrottle = CalculateThrottleDelay(account.FailedAttempts);

        return new AdminAuthAttemptResult
        {
            Success = false,
            IsLockedOut = false,
            RemainingAttemptsBeforeLockout = remainingAttempts,
            ThrottleDelayMs = nextThrottle,
            Message = $"Invalid credentials. {remainingAttempts} attempt(s) remaining before temporary lockout.",
            ClientIp = clientIp,
            AttemptedAtUtc = now
        };
    }

    public async Task<bool> SetPasswordAsync(string username, string newPassword, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        byte[] salt = GenerateSalt();
        byte[] hash = ComputeHash(newPassword, salt);

        var state = new AdminAccountState
        {
            Username = username,
            FailedAttempts = 0,
            LastFailedAtUtc = null,
            LockoutUntilUtc = null,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            Iterations = Iterations
        };

        _accounts[username] = state;

        await _auditService.LogAsync(
            AuditAction.SettingsChanged,
            username,
            succeeded: true,
            $"Administrative credentials updated for account '{username}' using NIST-compliant PBKDF2-HMAC-SHA256 (600,000 iterations).",
            ct: ct);

        return true;
    }

    public async Task ResetLockoutAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        if (_accounts.TryGetValue(username, out var account))
        {
            account.FailedAttempts = 0;
            account.LockoutUntilUtc = null;
            account.LastFailedAtUtc = null;

            await _auditService.LogAsync(
                AuditAction.AdminAccountUnlocked,
                username,
                succeeded: true,
                $"Administrative lockout manually reset for account '{username}'.",
                ct: ct);

            _logger.LogInformation("Administrative lockout cleared for account {Username}", username);
        }
    }

    public AdminAccountState GetAccountState(string username)
    {
        if (_accounts.TryGetValue(username, out var account))
        {
            return new AdminAccountState
            {
                Username = account.Username,
                FailedAttempts = account.FailedAttempts,
                LastFailedAtUtc = account.LastFailedAtUtc,
                LockoutUntilUtc = account.LockoutUntilUtc,
                Iterations = account.Iterations,
                Salt = account.Salt,
                PasswordHash = account.PasswordHash
            };
        }

        return new AdminAccountState { Username = username };
    }

    private void InitializeDefaultAccount(string username, string initialPassword)
    {
        byte[] salt = GenerateSalt();
        byte[] hash = ComputeHash(initialPassword, salt);

        _accounts[username] = new AdminAccountState
        {
            Username = username,
            FailedAttempts = 0,
            Salt = Convert.ToBase64String(salt),
            PasswordHash = Convert.ToBase64String(hash),
            Iterations = Iterations
        };
    }

    private static int CalculateThrottleDelay(int failedAttempts)
    {
        // NIST SP 800-63B § 5.2.2: Progressive backoff delay
        return failedAttempts switch
        {
            <= 2 => 0,          // First 2 attempts: no delay
            3 => 1000,          // 3rd failure: 1 second delay
            4 => 2500,          // 4th failure: 2.5 seconds delay
            _ => 5000           // 5th+ failure: 5 seconds delay before lockout check
        };
    }

    private static byte[] GenerateSalt()
    {
        return RandomNumberGenerator.GetBytes(SaltSizeBytes);
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);
    }
}
