using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkSelectionService : INetworkSelectionService
{
    private readonly ILogger<NetworkSelectionService> _logger;
    private readonly object _lock = new();
    private NetworkInterfaceInfo? _currentScanningInterface;
    private NetworkInterfaceInfo? _cctvInterface;
    private WifiConnectionInfo? _currentCctvWifiConnection;

    public NetworkInterfaceInfo? CurrentScanningInterface
    {
        get
        {
            lock (_lock)
            {
                return _currentScanningInterface;
            }
        }
        private set
        {
            lock (_lock)
            {
                _currentScanningInterface = value;
            }
        }
    }

    public NetworkInterfaceInfo? CctvInterface
    {
        get
        {
            lock (_lock)
            {
                return _cctvInterface;
            }
        }
        private set
        {
            lock (_lock)
            {
                _cctvInterface = value;
            }
        }
    }

    public WifiConnectionInfo? CurrentCctvWifiConnection
    {
        get
        {
            lock (_lock)
            {
                return _currentCctvWifiConnection;
            }
        }
        private set
        {
            lock (_lock)
            {
                _currentCctvWifiConnection = value;
            }
        }
    }

    public event Action<NetworkInterfaceInfo?>? SelectionChanged;
    public event Action<NetworkInterfaceInfo?>? CctvInterfaceChanged;

    public NetworkSelectionService(ILogger<NetworkSelectionService> logger)
    {
        _logger = logger;
    }

    public void SelectScanningInterface(NetworkInterfaceInfo iface)
    {
        ArgumentNullException.ThrowIfNull(iface);

        lock (_lock)
        {
            if (_currentScanningInterface?.Id == iface.Id &&
                _currentScanningInterface?.IPv4Address == iface.IPv4Address)
            {
                return;
            }

            _currentScanningInterface = iface;
        }

        _logger.LogInformation("App-scoped camera scanning interface selected: {Name} ({Ip}) [{Port}]",
            iface.Name, iface.IPv4Address, iface.HardwarePort);

        try
        {
            SelectionChanged?.Invoke(iface);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error notifying SelectionChanged listeners");
        }
    }

    public void SetCctvInterface(NetworkInterfaceInfo? iface, WifiConnectionInfo? connection = null)
    {
        lock (_lock)
        {
            _cctvInterface = iface;
            _currentCctvWifiConnection = connection;
        }

        if (iface != null)
        {
            _logger.LogInformation("Dedicated CCTV interface set: {Name} ({Ip}) [{Port}] - SSID: {Ssid}",
                iface.Name, iface.IPv4Address, iface.HardwarePort, connection?.Ssid ?? "N/A");
        }
        else
        {
            _logger.LogInformation("Dedicated CCTV interface cleared");
        }

        try
        {
            CctvInterfaceChanged?.Invoke(iface);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error notifying CctvInterfaceChanged listeners");
        }
    }
}
