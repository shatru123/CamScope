using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class MacWifiNetworkManager : IWifiNetworkManager
{
    private readonly INetworkInventoryService _inventoryService;
    private readonly INetworkSelectionService _selectionService;
    private readonly IAuditService _auditService;
    private readonly ILogger<MacWifiNetworkManager> _logger;
    private readonly ICredentialStore? _credentialStore;
    private readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(25);

    public bool IsPlatformSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public bool CanSupportConcurrentConnections
    {
        get
        {
            try
            {
                var upPhysical = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                                i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                !i.Name.StartsWith("lo", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return upPhysical.Count > 1;
            }
            catch
            {
                return false;
            }
        }
    }

    public MacWifiNetworkManager(
        INetworkInventoryService inventoryService,
        INetworkSelectionService selectionService,
        IAuditService auditService,
        ILogger<MacWifiNetworkManager> logger,
        ICredentialStore? credentialStore = null)
    {
        _inventoryService = inventoryService;
        _selectionService = selectionService;
        _auditService = auditService;
        _logger = logger;
        _credentialStore = credentialStore;
    }

    public static (WifiConnectionState State, string CleanMessage, string? ErrorClassification, bool CanRetry, bool IsUnsupportedAuthMode, string? SuggestedAlternative) ClassifyNetworksetupError(string combinedOutput, int exitCode)
    {
        string text = combinedOutput?.Trim() ?? string.Empty;

        // 1. Enterprise / 802.1X
        if (text.Contains("802.1X", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("802.1x", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("EAP", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("(-3915)", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WifiConnectionState.AuthenticationFailed,
                "This network uses 802.1X Enterprise authentication, which requires an operating system profile or certificate. Use macOS System Settings to connect.",
                "Unsupported 802.1X Enterprise Authentication Mode (-3915)",
                false,
                true,
                "Enterprise networks require profile/certificate installation. Connect through macOS System Settings > Wi-Fi."
            );
        }

        // 2. Authentication Failure / Password Incorrect (CoreWLAN -3905, -3906, -3907, -3924, -3925)
        if (text.Contains("-3905", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3906", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3907", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3924", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3925", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to join network", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Password incorrect", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WifiConnectionState.AuthenticationFailed,
                "Authentication failed. Check the password and try again.",
                "OS CoreWLAN Authentication Failure (-3905 / Password Incorrect or 4-way Handshake Mismatch)",
                true,
                false,
                "Check the WPA2/WPA3 password. If the password is unknown, consider switching to an authorized wired Ethernet interface or consulting the network administrator."
            );
        }

        // 3. Network Unavailable / Out of Range (CoreWLAN -3900, -3901, -3902)
        if (text.Contains("-3900", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3901", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("-3902", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Could not find network", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No network with that name", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WifiConnectionState.NetworkUnavailable,
                "Could not find the specified Wi-Fi network. The access point may be out of range, hidden, or offline.",
                "OS CoreWLAN Network Unavailable (-3900 / SSID Not Found)",
                true,
                false,
                "Verify the router is powered on, broadcasting its SSID, and within physical range."
            );
        }

        // 4. Permission Denied / Admin Required (CoreWLAN -3903)
        if (text.Contains("-3903", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("You must be an administrator", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return (
                WifiConnectionState.PermissionDenied,
                "Permission denied. Administrator privileges or system authorization required to join this Wi-Fi network.",
                "OS Permission Denied (-3903 / Administrative Privilege Required)",
                false,
                false,
                "Ensure CamScope has necessary permissions or grant network access in System Settings > Privacy & Security."
            );
        }

        // 5. General / Unclassified error
        string fallbackMsg = string.IsNullOrWhiteSpace(text)
            ? $"Failed to associate with network (Exit code {exitCode})."
            : text;

        return (
            WifiConnectionState.AuthenticationFailed,
            fallbackMsg,
            $"OS Network Error (Code {exitCode})",
            true,
            false,
            null
        );
    }

    public async Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default)
    {
        var wifiInterfaces = new List<NetworkInterfaceInfo>();

        if (!IsPlatformSupported)
            return wifiInterfaces;

        try
        {
            var report = await _inventoryService.GetNetworkInventoryAsync(forceRefresh: false, ct).ConfigureAwait(false);
            foreach (var iface in report.Interfaces)
            {
                if (iface.InterfaceKind == NetworkInterfaceKind.WiFi ||
                    (iface.HardwarePort ?? string.Empty).Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                    (iface.HardwarePort ?? string.Empty).Contains("AirPort", StringComparison.OrdinalIgnoreCase))
                {
                    wifiInterfaces.Add(iface);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Wi-Fi interfaces on macOS");
        }

        return wifiInterfaces;
    }

    public async Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default)
    {
        if (!IsPlatformSupported)
            return [];

        try
        {
            var report = await _inventoryService.GetNetworkInventoryAsync(forceRefresh: true, ct).ConfigureAwait(false);
            return report.WifiNetworks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan nearby Wi-Fi networks");
            return [];
        }
    }

    public async Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default)
    {
        var connections = new List<WifiConnectionInfo>();

        if (!IsPlatformSupported)
            return connections;

        try
        {
            var wifiInterfaces = await GetWifiInterfacesAsync(ct).ConfigureAwait(false);
            var report = await _inventoryService.GetNetworkInventoryAsync(forceRefresh: false, ct).ConfigureAwait(false);

            foreach (var iface in wifiInterfaces)
            {
                var (isConnected, ssid) = await QueryAirportAssociationAsync(iface.Name, ct).ConfigureAwait(false);
                if (isConnected && !string.IsNullOrEmpty(ssid))
                {
                    connections.Add(new WifiConnectionInfo
                    {
                        Ssid = ssid,
                        InterfaceName = iface.Name,
                        IPv4Address = iface.IPv4Address,
                        IsPrimaryInternet = iface.IsPrimaryInternet || (report.PrimaryInterface?.Name == iface.Name),
                        IsCctvNetwork = _selectionService.CctvInterface?.Name == iface.Name,
                        ConnectedAtUtc = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve current Wi-Fi connections");
        }

        return connections;
    }

    public async Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default)
    {
        if (!IsPlatformSupported || string.IsNullOrWhiteSpace(interfaceName))
            return WifiConnectionStatus.Unsupported;

        try
        {
            var (isConnected, _) = await QueryAirportAssociationAsync(interfaceName, ct).ConfigureAwait(false);
            return isConnected ? WifiConnectionStatus.Connected : WifiConnectionStatus.Disconnected;
        }
        catch
        {
            return WifiConnectionStatus.Failed;
        }
    }

    public async Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsPlatformSupported)
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.PermissionDenied,
                Message = "Wi-Fi management is not supported on this operating system."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Ssid))
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.AwaitingCredentials,
                CanRetry = true,
                Message = "SSID name cannot be empty."
            };
        }

        var wifiInterfaces = await GetWifiInterfacesAsync(ct).ConfigureAwait(false);
        var targetInterface = !string.IsNullOrWhiteSpace(request.InterfaceName)
            ? wifiInterfaces.FirstOrDefault(i => i.Name.Equals(request.InterfaceName, StringComparison.OrdinalIgnoreCase))
            : wifiInterfaces.FirstOrDefault();

        if (targetInterface == null)
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.NetworkUnavailable,
                CanRetry = false,
                Message = $"No compatible Wi-Fi interface found matching '{request.InterfaceName ?? "default"}'. Verify Wi-Fi adapter is connected."
            };
        }

        // Single physical Wi-Fi radio concurrency check
        bool hasMultipleAdapters = CanSupportConcurrentConnections;
        if (!hasMultipleAdapters && !request.AllowSwitchingPrimaryIfSingleAdapter)
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.AwaitingCredentials,
                ConnectedInterface = targetInterface.Name,
                PreservedPrimaryConnection = true,
                CanRetry = true,
                Message = $"Your Mac only has one active physical network adapter ({targetInterface.Name}). Connecting to \"{request.Ssid}\" will switch your primary internet connection. Attach a secondary USB-C Ethernet or Wi-Fi adapter for an isolated connection, or confirm switching."
            };
        }

        // Capture pre-connection routing snapshot
        NetworkRoutingSnapshot? beforeSnapshot = null;
        try
        {
            beforeSnapshot = await _inventoryService.CaptureRoutingSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not capture pre-connection routing snapshot");
        }

        // Check if saved credentials exist in Keychain if password was not provided
        string? effectivePassword = request.Password;
        if (string.IsNullOrEmpty(effectivePassword) && _credentialStore != null)
        {
            try
            {
                var savedCreds = await _credentialStore.GetCredentialsAsync($"wifi:{request.Ssid}", ct).ConfigureAwait(false);
                if (savedCreds.HasValue && !string.IsNullOrEmpty(savedCreds.Value.Password))
                {
                    effectivePassword = savedCreds.Value.Password;
                    _logger.LogInformation("Retrieved saved Keychain credentials for SSID {Ssid}", request.Ssid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keychain lookup skipped for {Ssid}", request.Ssid);
            }
        }

        // Redacted logging: NEVER log passwords
        _logger.LogInformation("Initiating Wi-Fi connection on interface {Interface} to SSID {Ssid}",
            targetInterface.Name, request.Ssid);

        await _auditService.LogAsync(
            AuditAction.NetworkInterfaceSelected,
            request.Ssid,
            succeeded: true,
            $"Operator requested Wi-Fi connection on interface '{targetInterface.Name}' to SSID '{request.Ssid}' (SingleAdapterWarningAccepted: {request.AllowSwitchingPrimaryIfSingleAdapter})",
            ct: ct).ConfigureAwait(false);

        // Execute networksetup -setairportnetwork <device> <SSID> [password]
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "networksetup",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-setairportnetwork");
            psi.ArgumentList.Add(targetInterface.Name);
            psi.ArgumentList.Add(request.Ssid);

            if (!string.IsNullOrEmpty(effectivePassword))
            {
                psi.ArgumentList.Add(effectivePassword);
            }

            process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_processTimeout);

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

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
                catch { /* ignore */ }
                throw;
            }

            var stdout = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            var combinedOutput = $"{stdout}\n{stderr}".Trim();

            // Evaluate connection result
            if (process.ExitCode != 0 ||
                combinedOutput.Contains("Could not find network", StringComparison.OrdinalIgnoreCase) ||
                combinedOutput.Contains("Failed to join", StringComparison.OrdinalIgnoreCase) ||
                combinedOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                var (state, cleanMessage, errorClassification, canRetry, isUnsupported, alt) =
                    ClassifyNetworksetupError(combinedOutput, process.ExitCode);

                _logger.LogWarning("Wi-Fi association failed on {Interface}: {Error} (Class: {Class})",
                    targetInterface.Name, cleanMessage, errorClassification);

                await _auditService.LogAsync(
                    AuditAction.NetworkInterfaceSelected,
                    request.Ssid,
                    succeeded: false,
                    $"Wi-Fi association to '{request.Ssid}' failed: {cleanMessage} ({errorClassification})",
                    ct: ct).ConfigureAwait(false);

                // Invariant verification on failure path
                var failureInvariant = await SafeCaptureAndVerifyInvariantAsync(beforeSnapshot, ct).ConfigureAwait(false);

                return new WifiConnectionResult
                {
                    Succeeded = false,
                    State = state,
                    ConnectedInterface = targetInterface.Name,
                    Message = cleanMessage,
                    ErrorClassification = errorClassification,
                    CanRetry = canRetry,
                    IsUnsupportedAuthMode = isUnsupported,
                    SuggestedAlternative = alt,
                    InvariantResult = failureInvariant,
                    PreservedPrimaryConnection = failureInvariant.IsStable
                };
            }

            // Brief delay to allow DHCP lease acquisition
            await Task.Delay(1200, ct).ConfigureAwait(false);

            // Capture post-connection routing snapshot & verify invariant
            var invariant = await SafeCaptureAndVerifyInvariantAsync(beforeSnapshot, ct).ConfigureAwait(false);

            // Evaluate primary connection preservation
            bool primaryPreserved = true;
            if (hasMultipleAdapters)
            {
                // On multi-adapter systems, default gateway and primary interface must remain unchanged
                primaryPreserved = invariant.IsStable;
            }
            else
            {
                // On single-adapter systems, switching was authorized by the operator
                primaryPreserved = !hasMultipleAdapters;
            }

            var connectionInfo = new WifiConnectionInfo
            {
                Ssid = request.Ssid,
                InterfaceName = targetInterface.Name,
                IPv4Address = invariant.AfterSnapshot?.DefaultGateway,
                IsPrimaryInternet = !hasMultipleAdapters || targetInterface.IsPrimaryInternet,
                IsCctvNetwork = true,
                ConnectedAtUtc = DateTime.UtcNow
            };

            // Register as active CCTV interface in selection service
            _selectionService.SetCctvInterface(targetInterface, connectionInfo);

            // Save credentials to Keychain if user opted in
            if (request.RememberInKeychain && _credentialStore != null && !string.IsNullOrEmpty(effectivePassword))
            {
                try
                {
                    await _credentialStore.SaveCredentialsAsync($"wifi:{request.Ssid}", "wifi", effectivePassword, ct).ConfigureAwait(false);
                    _logger.LogInformation("Stored Wi-Fi credentials in OS Keychain for SSID {Ssid}", request.Ssid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist credentials to OS Keychain for SSID {Ssid}", request.Ssid);
                }
            }

            await _auditService.LogAsync(
                AuditAction.NetworkInterfaceSelected,
                request.Ssid,
                succeeded: true,
                $"Successfully associated interface '{targetInterface.Name}' with SSID '{request.Ssid}'. Primary connection preserved: {primaryPreserved}.",
                ct: ct).ConfigureAwait(false);

            return new WifiConnectionResult
            {
                Succeeded = true,
                State = WifiConnectionState.Connected,
                ConnectedInterface = targetInterface.Name,
                PreservedPrimaryConnection = primaryPreserved,
                InvariantResult = invariant,
                CanRetry = false,
                Message = $"Connected to \"{request.Ssid}\" on {targetInterface.Name}. {invariant.StatusText}"
            };
        }
        catch (OperationCanceledException)
        {
            bool isExplicitCancel = ct.IsCancellationRequested;
            var cancelledState = isExplicitCancel ? WifiConnectionState.Cancelled : WifiConnectionState.TimedOut;
            var cancelledMessage = isExplicitCancel
                ? "Connection cancelled by operator."
                : "Connection attempt timed out waiting for association or DHCP lease.";

            _logger.LogInformation("Wi-Fi connection to {Ssid} terminated: {State}", request.Ssid, cancelledState);

            var cancelInvariant = await SafeCaptureAndVerifyInvariantAsync(beforeSnapshot, CancellationToken.None).ConfigureAwait(false);

            return new WifiConnectionResult
            {
                Succeeded = false,
                State = cancelledState,
                ConnectedInterface = targetInterface.Name,
                CanRetry = true,
                Message = cancelledMessage,
                InvariantResult = cancelInvariant,
                PreservedPrimaryConnection = cancelInvariant.IsStable
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while connecting to Wi-Fi network {Ssid}", request.Ssid);

            var errInvariant = await SafeCaptureAndVerifyInvariantAsync(beforeSnapshot, CancellationToken.None).ConfigureAwait(false);

            return new WifiConnectionResult
            {
                Succeeded = false,
                State = WifiConnectionState.AuthenticationFailed,
                ConnectedInterface = targetInterface.Name,
                CanRetry = true,
                Message = $"Connection failed: {ex.Message}",
                InvariantResult = errInvariant,
                PreservedPrimaryConnection = errInvariant.IsStable
            };
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<RoutingInvariantResult> SafeCaptureAndVerifyInvariantAsync(NetworkRoutingSnapshot? beforeSnapshot, CancellationToken ct)
    {
        try
        {
            var afterSnapshot = await _inventoryService.CaptureRoutingSnapshotAsync(ct).ConfigureAwait(false);
            return _inventoryService.VerifyRoutingInvariant(beforeSnapshot, afterSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not capture post-connection routing snapshot for invariant check");
            return new RoutingInvariantResult
            {
                IsStable = true,
                VerificationSucceeded = false,
                StatusText = "Could not verify post-connection routing invariant."
            };
        }
    }

    public async Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default)
    {
        if (!IsPlatformSupported || string.IsNullOrWhiteSpace(interfaceName))
            return false;

        try
        {
            _logger.LogInformation("Disconnecting Wi-Fi interface {Interface}", interfaceName);

            // Power cycle the airport radio to disassociate cleanly
            await RunProcessAsync("networksetup", $"-setairportpower {interfaceName} off", ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            await RunProcessAsync("networksetup", $"-setairportpower {interfaceName} on", ct).ConfigureAwait(false);

            if (_selectionService.CctvInterface?.Name == interfaceName)
            {
                _selectionService.SetCctvInterface(null, null);
            }

            await _auditService.LogAsync(
                AuditAction.NetworkInterfaceSelected,
                interfaceName,
                succeeded: true,
                $"Disconnected Wi-Fi interface '{interfaceName}'",
                ct: ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Wi-Fi interface {Interface}", interfaceName);
            return false;
        }
    }

    private async Task<(bool isConnected, string? ssid)> QueryAirportAssociationAsync(string interfaceName, CancellationToken ct)
    {
        try
        {
            var output = await RunProcessAsync("networksetup", $"-getairportnetwork {interfaceName}", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output) || output.Contains("not associated", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            var match = Regex.Match(output, @"Current Wi-Fi Network:\s*(.+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return (true, match.Groups[1].Value.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Query airport association failed on {Interface}", interfaceName);
        }

        return (false, null);
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_processTimeout);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        return await outputTask.ConfigureAwait(false);
    }
}
