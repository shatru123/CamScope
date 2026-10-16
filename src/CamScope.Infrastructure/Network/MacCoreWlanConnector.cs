using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public record CoreWlanAssociationResult(
    bool Succeeded,
    int ExitCode,
    string Output,
    string? ErrorClassification = null,
    int? CoreWlanErrorCode = null
);

public interface IMacCoreWlanConnector
{
    Task<CoreWlanAssociationResult> AssociateAsync(string interfaceName, string ssid, string? password, CancellationToken ct = default);
    Task<bool> DisassociateAsync(string interfaceName, CancellationToken ct = default);
}

public sealed class MacCoreWlanConnector : IMacCoreWlanConnector
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(25);

    // Swift script that interfaces directly with CoreWLAN.
    // Interface name and SSID are provided via CommandLine.arguments.
    // The password is read strictly via standard input (readLine) to avoid process argv disclosure (CWE-214).
    private const string SwiftAssociationScript = @"
import CoreWLAN
import Foundation

guard CommandLine.arguments.count >= 3 else {
    fputs(""Error: Insufficient arguments. Expected <interface> <ssid>\n"", stderr)
    exit(1)
}

let ifaceName = CommandLine.arguments[1]
let targetSsid = CommandLine.arguments[2]
let password = readLine(strippingNewline: true)

guard let iface = CWWiFiClient.shared().interface(withName: ifaceName) else {
    fputs(""Error: Interface \(ifaceName) not found (-3900)\n"", stderr)
    exit(2)
}

do {
    let scanResults = try iface.scanForNetworks(withName: targetSsid, includeHidden: true)
    guard let network = scanResults.max(by: { $0.rssiValue < $1.rssiValue }) else {
        fputs(""Error: Could not find network (-3900): \(targetSsid)\n"", stderr)
        exit(3)
    }

    let passToUse = (password != nil && !password!.isEmpty) ? password : nil
    try iface.associate(to: network, password: passToUse)
    print(""ASSOCIATION_SUCCESS"")
    exit(0)
} catch let error as NSError {
    fputs(""CoreWLAN Error: \(error.domain) (\(error.code)) - \(error.localizedDescription)\n"", stderr)
    exit(4)
} catch {
    fputs(""Unknown error: \(error)\n"", stderr)
    exit(5)
}
";

    private const string SwiftDisassociationScript = @"
import CoreWLAN
import Foundation

guard CommandLine.arguments.count >= 2 else { exit(1) }
let ifaceName = CommandLine.arguments[1]
if let iface = CWWiFiClient.shared().interface(withName: ifaceName) {
    iface.disassociate()
    print(""DISASSOCIATION_SUCCESS"")
    exit(0)
}
exit(2)
";

    public MacCoreWlanConnector(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<CoreWlanAssociationResult> AssociateAsync(string interfaceName, string ssid, string? password, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new CoreWlanAssociationResult(false, -1, "CoreWLAN association is only supported on macOS.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ssid);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/swift",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Command line arguments NEVER contain the password
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(SwiftAssociationScript);
            psi.ArgumentList.Add(interfaceName);
            psi.ArgumentList.Add(ssid);

            process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            process.Start();

            // Securely transmit password through stdin only
            if (!string.IsNullOrEmpty(password))
            {
                await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
            }
            else
            {
                await process.StandardInput.WriteLineAsync(string.Empty).ConfigureAwait(false);
            }
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { /* ignore kill failure */ }
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combinedOutput = $"{stdout}\n{stderr}".Trim();

            int? errorCode = ExtractErrorCode(combinedOutput);
            bool success = process.ExitCode == 0 && combinedOutput.Contains("ASSOCIATION_SUCCESS");

            return new CoreWlanAssociationResult(
                success,
                process.ExitCode,
                combinedOutput,
                errorCode.HasValue ? $"CoreWLAN Error ({errorCode})" : null,
                errorCode
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to execute CoreWLAN association via Swift runner");
            return new CoreWlanAssociationResult(false, -1, $"Failed to launch CoreWLAN association: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    public async Task<bool> DisassociateAsync(string interfaceName, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || string.IsNullOrWhiteSpace(interfaceName))
            return false;

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/swift",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(SwiftDisassociationScript);
            psi.ArgumentList.Add(interfaceName);

            process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && output.Contains("DISASSOCIATION_SUCCESS"))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Swift disassociate returned error, falling back to radio cycle");
        }
        finally
        {
            process?.Dispose();
        }

        // Fallback: networksetup radio toggle
        try
        {
            var offPsi = new ProcessStartInfo
            {
                FileName = "networksetup",
                Arguments = $"-setairportpower {interfaceName} off",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pOff = Process.Start(offPsi);
            if (pOff != null) await pOff.WaitForExitAsync(ct).ConfigureAwait(false);

            await Task.Delay(300, ct).ConfigureAwait(false);

            var onPsi = new ProcessStartInfo
            {
                FileName = "networksetup",
                Arguments = $"-setairportpower {interfaceName} on",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pOn = Process.Start(onPsi);
            if (pOn != null) await pOn.WaitForExitAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to power cycle Wi-Fi radio on {Interface}", interfaceName);
            return false;
        }
    }

    private static int? ExtractErrorCode(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var match = Regex.Match(output, @"\(-?(\d+)\)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int code))
        {
            return -code;
        }

        return null;
    }
}
