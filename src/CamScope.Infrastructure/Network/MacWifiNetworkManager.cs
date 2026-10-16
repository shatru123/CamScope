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
        ILogger<MacWifiNetworkManager> logger)
    {
        _inventoryService = inventoryService;
        _selectionService = selectionService;
        _auditService = auditService;
        _logger = logger;
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
                Message = "Wi-Fi management is not supported on this operating system."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Ssid))
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
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
                ConnectedInterface = targetInterface.Name,
                PreservedPrimaryConnection = true,
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

            if (!string.IsNullOrEmpty(request.Password))
            {
                psi.ArgumentList.Add(request.Password);
            }

            using var process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_processTimeout);

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            var combinedOutput = $"{stdout}\n{stderr}".Trim();

            // Evaluate connection result
            if (process.ExitCode != 0 ||
                combinedOutput.Contains("Could not find network", StringComparison.OrdinalIgnoreCase) ||
                combinedOutput.Contains("Failed to join", StringComparison.OrdinalIgnoreCase) ||
                combinedOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                string cleanError = string.IsNullOrWhiteSpace(combinedOutput)
                    ? $"Failed to associate with \"{request.Ssid}\" (Exit code {process.ExitCode})."
                    : combinedOutput;

                _logger.LogWarning("Wi-Fi association failed on {Interface}: {Error}", targetInterface.Name, cleanError);

                await _auditService.LogAsync(
                    AuditAction.NetworkInterfaceSelected,
                    request.Ssid,
                    succeeded: false,
                    $"Wi-Fi association to '{request.Ssid}' failed: {cleanError}",
                    ct: ct).ConfigureAwait(false);

                return new WifiConnectionResult
                {
                    Succeeded = false,
                    ConnectedInterface = targetInterface.Name,
                    Message = cleanError
                };
            }

            // Brief delay to allow DHCP lease acquisition
            await Task.Delay(1200, ct).ConfigureAwait(false);

            // Capture post-connection routing snapshot & verify invariant
            NetworkRoutingSnapshot? afterSnapshot = null;
            try
            {
                afterSnapshot = await _inventoryService.CaptureRoutingSnapshotAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not capture post-connection routing snapshot");
            }

            var invariant = _inventoryService.VerifyRoutingInvariant(beforeSnapshot, afterSnapshot);

            // Evaluate primary connection preservation
            bool primaryPreserved = true;
            if (hasMultipleAdapters)
            {
                // On multi-adapter systems, the default gateway and primary interface must remain unchanged
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
                IPv4Address = afterSnapshot?.DefaultGateway,
                IsPrimaryInternet = !hasMultipleAdapters || targetInterface.IsPrimaryInternet,
                IsCctvNetwork = true,
                ConnectedAtUtc = DateTime.UtcNow
            };

            // Register as active CCTV interface in selection service
            _selectionService.SetCctvInterface(targetInterface, connectionInfo);

            await _auditService.LogAsync(
                AuditAction.NetworkInterfaceSelected,
                request.Ssid,
                succeeded: true,
                $"Successfully associated interface '{targetInterface.Name}' with SSID '{request.Ssid}'. Primary connection preserved: {primaryPreserved}.",
                ct: ct).ConfigureAwait(false);

            return new WifiConnectionResult
            {
                Succeeded = true,
                ConnectedInterface = targetInterface.Name,
                PreservedPrimaryConnection = primaryPreserved,
                InvariantResult = invariant,
                Message = $"Connected to \"{request.Ssid}\" on {targetInterface.Name}. {invariant.StatusText}"
            };
        }
        catch (OperationCanceledException)
        {
            return new WifiConnectionResult
            {
                Succeeded = false,
                ConnectedInterface = targetInterface.Name,
                Message = "Connection attempt timed out or was cancelled by operator."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while connecting to Wi-Fi network");
            return new WifiConnectionResult
            {
                Succeeded = false,
                ConnectedInterface = targetInterface.Name,
                Message = $"Connection failed: {ex.Message}"
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
