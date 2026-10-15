using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CamScope.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Security;

public sealed class SecureCredentialStore : ICredentialStore
{
    private const string ServiceName = "com.camscope.credentials";
    private readonly ConcurrentDictionary<string, (string Username, string Password)> _inMemoryFallback = new();
    private readonly ILogger<SecureCredentialStore> _logger;
    private readonly bool _isMac;

    public SecureCredentialStore(ILogger<SecureCredentialStore> logger)
    {
        _logger = logger;
        _isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    public async Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        _inMemoryFallback[endpointId] = (username, password);

        if (_isMac)
        {
            try
            {
                // Format: "username:::password" in Keychain secret payload
                var payload = $"{username}:::{password}";
                await RunSecurityCommandAsync(
                    $"add-generic-password -a \"{EscapeQuotes(endpointId)}\" -s \"{ServiceName}\" -w \"{EscapeQuotes(payload)}\" -U",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keychain save unavailable. Retaining in secure memory store.");
            }
        }
    }

    public async Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        if (_inMemoryFallback.TryGetValue(endpointId, out var creds))
        {
            return creds;
        }

        if (_isMac)
        {
            try
            {
                var output = await RunSecurityCommandAsync(
                    $"find-generic-password -a \"{EscapeQuotes(endpointId)}\" -s \"{ServiceName}\" -w",
                    ct);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var parts = output.Trim().Split(":::", 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        var tuple = (parts[0], parts[1]);
                        _inMemoryFallback[endpointId] = tuple;
                        return tuple;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keychain lookup unavailable for {Endpoint}", endpointId);
            }
        }

        return null;
    }

    public async Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        _inMemoryFallback.TryRemove(endpointId, out _);

        if (_isMac)
        {
            try
            {
                await RunSecurityCommandAsync(
                    $"delete-generic-password -a \"{EscapeQuotes(endpointId)}\" -s \"{ServiceName}\"",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keychain deletion skipped for {Endpoint}", endpointId);
            }
        }
    }

    private static async Task<string> RunSecurityCommandAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "security",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return await stdoutTask;
    }

    private static string EscapeQuotes(string val) => val.Replace("\"", "\\\"");
}
