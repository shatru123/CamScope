using System.Runtime.InteropServices;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class LinuxWifiNetworkManager : IWifiNetworkManager
{
    private readonly ILogger<LinuxWifiNetworkManager> _logger;

    public bool IsPlatformSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool CanSupportConcurrentConnections => false;

    public LinuxWifiNetworkManager(ILogger<LinuxWifiNetworkManager> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiNetworkInfo>>([]);

    public Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiConnectionInfo>>([]);

    public Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkInterfaceInfo>>([]);

    public Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new WifiConnectionResult
        {
            Succeeded = false,
            State = WifiConnectionState.PermissionDenied,
            Message = "Linux NetworkManager integration is not configured on this host."
        });
    }

    public Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(WifiConnectionStatus.Unsupported);
}

public sealed class WindowsWifiNetworkManager : IWifiNetworkManager
{
    private readonly ILogger<WindowsWifiNetworkManager> _logger;

    public bool IsPlatformSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool CanSupportConcurrentConnections => false;

    public WindowsWifiNetworkManager(ILogger<WindowsWifiNetworkManager> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiNetworkInfo>>([]);

    public Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiConnectionInfo>>([]);

    public Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkInterfaceInfo>>([]);

    public Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new WifiConnectionResult
        {
            Succeeded = false,
            State = WifiConnectionState.PermissionDenied,
            Message = "Windows WLAN service integration is not configured on this host."
        });
    }

    public Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(WifiConnectionStatus.Unsupported);
}

public sealed class UnsupportedPlatformWifiNetworkManager : IWifiNetworkManager
{
    public bool IsPlatformSupported => false;
    public bool CanSupportConcurrentConnections => false;

    public Task<IReadOnlyList<WifiNetworkInfo>> GetAvailableNetworksAsync(string? interfaceName = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiNetworkInfo>>([]);

    public Task<IReadOnlyList<WifiConnectionInfo>> GetCurrentConnectionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WifiConnectionInfo>>([]);

    public Task<IReadOnlyList<NetworkInterfaceInfo>> GetWifiInterfacesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NetworkInterfaceInfo>>([]);

    public Task<WifiConnectionResult> ConnectAsync(WifiConnectRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new WifiConnectionResult
        {
            Succeeded = false,
            State = WifiConnectionState.PermissionDenied,
            Message = "Wi-Fi management is not supported on this platform."
        });
    }

    public Task<bool> DisconnectAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<WifiConnectionStatus> GetConnectionStatusAsync(string interfaceName, CancellationToken ct = default)
        => Task.FromResult(WifiConnectionStatus.Unsupported);
}
