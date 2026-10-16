using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkSelectionService : INetworkSelectionService
{
    private readonly ILogger<NetworkSelectionService> _logger;
    private readonly object _lock = new();
    private NetworkInterfaceInfo? _currentScanningInterface;

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

    public event Action<NetworkInterfaceInfo?>? SelectionChanged;

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
}
