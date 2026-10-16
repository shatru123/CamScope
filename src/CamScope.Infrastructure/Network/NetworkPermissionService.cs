using System.Runtime.InteropServices;
using CamScope.Core.Interfaces;

namespace CamScope.Infrastructure.Network;

public sealed class NetworkPermissionService : INetworkPermissionService
{
    public Task<bool> HasWifiScanningPermissionAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // On macOS 10.15+ Catalina through macOS 15+ Sequoia, access to nearby Wi-Fi network SSIDs
            // and BSSIDs requires CoreWLAN / Location Services authorization.
            // CamScope inspects the system Wi-Fi report via supported macOS tools without modifying system configuration.
            return Task.FromResult(true);
        }

        return Task.FromResult(true);
    }

    public string GetPermissionExplanation()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "On macOS, enumerating nearby Wi-Fi networks and BSSIDs is protected by Apple privacy controls. " +
                   "CamScope reads available network telemetry through supported macOS diagnostic facilities. " +
                   "Connecting to a different Wi-Fi SSID is an operating system action managed by macOS System Settings.";
        }

        return "Local network access is managed by the operating system security policies.";
    }
}
