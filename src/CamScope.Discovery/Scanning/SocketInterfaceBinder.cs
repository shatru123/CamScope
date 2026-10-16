using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Discovery.Scanning;

public static class SocketInterfaceBinder
{
    // macOS Darwin constants
    private const int MacOS_IPPROTO_IP = 0;
    private const int MacOS_IP_BOUND_IF = 25;
    private const int MacOS_IPPROTO_IPV6 = 41;
    private const int MacOS_IPV6_BOUND_IF = 125;

    // Linux constants
    private const int Linux_SOL_SOCKET = 1;
    private const int Linux_SO_BINDTODEVICE = 25;

    // Windows constants
    private const int Windows_IPPROTO_IP = 0;
    private const int Windows_IP_UNICAST_IF = 31;

    internal static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "if_nametoindex", CharSet = CharSet.Ansi)]
        public static extern uint IfNameToIndex(string ifname);
    }

    public static uint ResolveInterfaceIndex(NetworkInterfaceInfo iface)
    {
        if (iface == null) return 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (!string.IsNullOrEmpty(iface.Name))
                {
                    uint idx = NativeMethods.IfNameToIndex(iface.Name);
                    if (idx > 0) return idx;
                }
            }
            catch
            {
                // Fall back to .NET properties
            }
        }

        if (iface.InterfaceIndex > 0)
        {
            return (uint)iface.InterfaceIndex;
        }

        try
        {
            foreach (var netIf in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netIf.Name.Equals(iface.Name, StringComparison.OrdinalIgnoreCase) ||
                    netIf.Id.Equals(iface.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var ipProps = netIf.GetIPProperties();
                    try
                    {
                        var ipv4 = ipProps.GetIPv4Properties();
                        if (ipv4?.Index > 0) return (uint)ipv4.Index;
                    }
                    catch { }

                    try
                    {
                        var ipv6 = ipProps.GetIPv6Properties();
                        if (ipv6?.Index > 0) return (uint)ipv6.Index;
                    }
                    catch { }
                }
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        return 0;
    }

    public static InterfaceBindingStatus BindSocketToInterface(
        Socket socket,
        NetworkInterfaceInfo iface,
        IPAddress localIp,
        bool isMulticast = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(iface);
        ArgumentNullException.ThrowIfNull(localIp);

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

        uint ifIndex = ResolveInterfaceIndex(iface);
        bool strictPinSucceeded = false;
        string bindingMethod = "Local IP Bind";
        string? limitationWarning = null;

        // 1. Attempt OS-specific interface egress pinning
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (ifIndex > 0)
            {
                try
                {
                    byte[] ifIndexBytes = BitConverter.GetBytes((int)ifIndex);
                    socket.SetRawSocketOption(MacOS_IPPROTO_IP, MacOS_IP_BOUND_IF, ifIndexBytes);
                    strictPinSucceeded = true;
                    bindingMethod = $"macOS IP_BOUND_IF ({iface.Name}, index {ifIndex})";
                    logger?.LogDebug("Successfully pinned socket to macOS interface {Name} (index {Index}) via IP_BOUND_IF", iface.Name, ifIndex);
                }
                catch (Exception ex)
                {
                    limitationWarning = $"Failed to set macOS IP_BOUND_IF for {iface.Name} (index {ifIndex}): {ex.Message}";
                    logger?.LogWarning(ex, "{Warning}", limitationWarning);
                }
            }
            else
            {
                limitationWarning = $"macOS interface index for '{iface.Name}' could not be resolved. Strict kernel-level egress pinning unavailable.";
                logger?.LogWarning("{Warning}", limitationWarning);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(iface.Name + "\0");
                socket.SetRawSocketOption(Linux_SOL_SOCKET, Linux_SO_BINDTODEVICE, nameBytes);
                strictPinSucceeded = true;
                bindingMethod = $"Linux SO_BINDTODEVICE ({iface.Name})";
                logger?.LogDebug("Successfully pinned socket to Linux device {Name} via SO_BINDTODEVICE", iface.Name);
            }
            catch (Exception ex)
            {
                limitationWarning = $"Failed to set Linux SO_BINDTODEVICE on {iface.Name}: {ex.Message}";
                logger?.LogWarning(ex, "{Warning}", limitationWarning);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (ifIndex > 0)
            {
                try
                {
                    int netOrderIndex = IPAddress.HostToNetworkOrder((int)ifIndex);
                    socket.SetRawSocketOption(Windows_IPPROTO_IP, Windows_IP_UNICAST_IF, BitConverter.GetBytes(netOrderIndex));
                    strictPinSucceeded = true;
                    bindingMethod = $"Windows IP_UNICAST_IF ({iface.Name}, index {ifIndex})";
                    logger?.LogDebug("Successfully set Windows IP_UNICAST_IF for {Name} (index {Index})", iface.Name, ifIndex);
                }
                catch (Exception ex)
                {
                    limitationWarning = $"Failed to set Windows IP_UNICAST_IF for {iface.Name}: {ex.Message}";
                    logger?.LogWarning(ex, "{Warning}", limitationWarning);
                }
            }
            else
            {
                limitationWarning = $"Windows interface index for '{iface.Name}' could not be resolved.";
                logger?.LogWarning("{Warning}", limitationWarning);
            }
        }
        else
        {
            limitationWarning = "OS-specific kernel interface egress pinning is not supported on this platform.";
        }

        // 2. Multicast outgoing interface configuration
        if (isMulticast)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    localIp.GetAddressBytes());
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not set MulticastInterface on {Name}", iface.Name);
            }
        }

        // 3. Bind socket to local IP
        try
        {
            socket.Bind(new IPEndPoint(localIp, 0));
        }
        catch (SocketException ex)
        {
            strictPinSucceeded = false;
            limitationWarning = $"Could not bind specifically to IP {localIp} ({ex.SocketErrorCode}): {ex.Message}. Falling back to IPAddress.Any.";
            logger?.LogWarning(ex, "Failed to bind socket to local IP {LocalIp}; attempting fallback to IPAddress.Any", localIp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        if (!strictPinSucceeded)
        {
            limitationWarning ??= $"Strict OS-level egress pinning was not confirmed. Socket is bound to local IP {localIp}; egress follows system routing table.";
        }

        return new InterfaceBindingStatus
        {
            IsStrictlyBound = strictPinSucceeded,
            InterfaceName = iface.Name,
            InterfaceIndex = (int)ifIndex,
            LocalIp = localIp.ToString(),
            BindingMethod = bindingMethod,
            LimitationWarning = limitationWarning
        };
    }

    public static InterfaceBindingStatus InspectBindingSupport(NetworkInterfaceInfo? iface)
    {
        if (iface == null)
        {
            return new InterfaceBindingStatus
            {
                IsStrictlyBound = false,
                InterfaceName = "None",
                BindingMethod = "None",
                LimitationWarning = "No interface selected."
            };
        }

        uint ifIndex = ResolveInterfaceIndex(iface);
        bool canStrictlyPin = false;
        string method = "Local IP Bind";
        string? warning = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (ifIndex > 0)
            {
                canStrictlyPin = true;
                method = $"macOS IP_BOUND_IF ({iface.Name}, index {ifIndex})";
            }
            else
            {
                warning = $"macOS interface index for '{iface.Name}' could not be resolved. Strict kernel-level egress pinning unavailable.";
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            canStrictlyPin = true;
            method = $"Linux SO_BINDTODEVICE ({iface.Name})";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (ifIndex > 0)
            {
                canStrictlyPin = true;
                method = $"Windows IP_UNICAST_IF ({iface.Name}, index {ifIndex})";
            }
            else
            {
                warning = $"Windows interface index for '{iface.Name}' could not be resolved.";
            }
        }
        else
        {
            warning = "Kernel interface egress pinning is not supported on this platform.";
        }

        if (!canStrictlyPin)
        {
            warning ??= $"Socket will bind to local IP {iface.IPv4Address}; egress follows system routing table.";
        }

        return new InterfaceBindingStatus
        {
            IsStrictlyBound = canStrictlyPin,
            InterfaceName = iface.Name,
            InterfaceIndex = (int)ifIndex,
            LocalIp = iface.IPv4Address,
            BindingMethod = method,
            LimitationWarning = warning
        };
    }
}
