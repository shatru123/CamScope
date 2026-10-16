using System.Net;
using System.Net.Sockets;
using System.Text;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Discovery.Deduplication;
using CamScope.Discovery.Parsing;
using Microsoft.Extensions.Logging;

namespace CamScope.Discovery.Engine;

public sealed class WsDiscoveryEngine : INetworkDiscoveryEngine
{
    private const string MulticastIPv4 = "239.255.255.250";
    private const int MulticastPort = 3702;
    private const int BufferSize = 65536;

    private readonly ILogger<WsDiscoveryEngine> _logger;
    private readonly DeviceDeduplicator _deduplicator = new();
    private CancellationTokenSource? _activeCts;

    public DiscoveryStatus Status { get; private set; } = DiscoveryStatus.Idle;

    public WsDiscoveryEngine(ILogger<WsDiscoveryEngine> logger)
    {
        _logger = logger;
    }

    public void Cancel()
    {
        _activeCts?.Cancel();
    }

    public async Task<IReadOnlyList<CameraDevice>> DiscoverAsync(
        NetworkInterfaceInfo iface,
        CancellationToken ct = default,
        IProgress<DiscoveryProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(iface);
        if (string.IsNullOrWhiteSpace(iface.IPv4Address) || !IPAddress.TryParse(iface.IPv4Address, out var localIp))
        {
            throw new ArgumentException($"Invalid IPv4 address on interface {iface.Name}: {iface.IPv4Address}", nameof(iface));
        }

        _deduplicator.Clear();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts = linkedCts;

        ReportProgress(progress, DiscoveryStatus.BindingInterface, 5, $"Binding to interface {iface.Name} ({iface.IPv4Address})...", 0);

        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            socket.Bind(new IPEndPoint(localIp, 0));

            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    localIp.GetAddressBytes());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not set MulticastInterface option for interface {Name}", iface.Name);
            }

            bool joinedMulticast = false;
            try
            {
                var multicastAddress = IPAddress.Parse(MulticastIPv4);
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(multicastAddress, localIp));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                joinedMulticast = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to join multicast group on interface {Name}: {Message}", iface.Name, ex.Message);
            }

            IPEndPoint targetEndpoint;
            if (joinedMulticast)
            {
                targetEndpoint = new IPEndPoint(IPAddress.Parse(MulticastIPv4), MulticastPort);
            }
            else
            {
                var broadcastIp = CalculateBroadcastAddress(localIp, iface.SubnetMask);
                targetEndpoint = new IPEndPoint(broadcastIp, MulticastPort);
                _logger.LogInformation("Multicast membership failed on interface {Name}. Falling back to directed broadcast {BroadcastIp}:{Port}",
                    iface.Name, broadcastIp, MulticastPort);
            }

            var probeMessage = BuildProbeMessage();
            var probeBytes = Encoding.UTF8.GetBytes(probeMessage);

            ReportProgress(progress, DiscoveryStatus.SendingProbes, 20,
                joinedMulticast
                    ? "Broadcasting WS-Discovery multicast probe packets..."
                    : $"Broadcasting WS-Discovery directed probe packets to {targetEndpoint}...", 0);

            int successfulProbes = 0;
            SocketException? lastSocketException = null;

            for (int i = 0; i < 2; i++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                try
                {
                    await socket.SendToAsync(probeBytes, SocketFlags.None, targetEndpoint, linkedCts.Token);
                    successfulProbes++;
                    await Task.Delay(200, linkedCts.Token);
                }
                catch (SocketException sex)
                {
                    lastSocketException = sex;
                    _logger.LogWarning(sex, "Probe transmission failure ({Attempt}/2) on {Name}: {Message}", i + 1, iface.Name, sex.Message);
                }
            }

            if (successfulProbes == 0)
            {
                var msg = $"Fatal socket error sending discovery probes on interface {iface.Name}: {lastSocketException?.Message ?? "All transmission attempts failed"}";
                _logger.LogError(lastSocketException, "{Message}", msg);
                ReportProgress(progress, DiscoveryStatus.Failed, 100, msg, 0);
                throw lastSocketException ?? new SocketException((int)SocketError.NetworkUnreachable);
            }

            ReportProgress(progress, DiscoveryStatus.AwaitingResponses, 40, "Awaiting responses from ONVIF devices...", 0);

            // Listen for responses
            var receiveBuffer = new byte[BufferSize];
            var listenTimeout = TimeSpan.FromSeconds(5);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < listenTimeout)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var remaining = listenTimeout - (DateTime.UtcNow - startTime);
                if (remaining <= TimeSpan.Zero) break;

                using var timeoutCts = new CancellationTokenSource(remaining);
                using var readLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, timeoutCts.Token);

                try
                {
                    var receiveResult = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0),
                        readLinkedCts.Token);

                    if (receiveResult.ReceivedBytes > 0)
                    {
                        var responseXml = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.ReceivedBytes);
                        ProcessResponse(responseXml, progress);
                    }
                }
                catch (OperationCanceledException) when (!linkedCts.Token.IsCancellationRequested)
                {
                    // Listen window expired normally
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Socket read exception during discovery");
                    break;
                }
            }

            ReportProgress(progress, DiscoveryStatus.Completed, 100, "Discovery complete.", _deduplicator.GetAll().Count);
            return _deduplicator.GetAll();
        }
        catch (OperationCanceledException)
        {
            ReportProgress(progress, DiscoveryStatus.Cancelled, 100, "Discovery was cancelled.", _deduplicator.GetAll().Count);
            return _deduplicator.GetAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WS-Discovery failed on interface {Interface}", iface.Name);
            ReportProgress(progress, DiscoveryStatus.Failed, 100, $"Discovery failed: {ex.Message}", _deduplicator.GetAll().Count);
            throw;
        }
        finally
        {
            _activeCts = null;
            socket?.Dispose();
        }
    }

    private void ProcessResponse(string responseXml, IProgress<DiscoveryProgressReport>? progress)
    {
        try
        {
            var devices = ProbeMatchParser.ParseProbeMatches(responseXml);
            foreach (var dev in devices)
            {
                var isNew = _deduplicator.TryAddOrUpdate(dev, out var current);
                if (isNew)
                {
                    _logger.LogInformation("Discovered device {DisplayName} at {Host}:{Port}", current.DisplayName, current.Host, current.Port);
                    ReportProgress(
                        progress,
                        DiscoveryStatus.ProcessingDevices,
                        60,
                        $"Found: {current.DisplayName} ({current.Host})",
                        _deduplicator.GetAll().Count,
                        current);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse discovery response packet");
        }
    }

    private void ReportProgress(
        IProgress<DiscoveryProgressReport>? progress,
        DiscoveryStatus status,
        int percentage,
        string step,
        int devicesFound,
        CameraDevice? latest = null)
    {
        Status = status;
        progress?.Report(new DiscoveryProgressReport
        {
            Status = status,
            Percentage = percentage,
            CurrentStep = step,
            DevicesFound = devicesFound,
            LatestDevice = latest
        });
    }

    public static string BuildProbeMessage(string? messageId = null)
    {
        var msgId = messageId ?? Guid.NewGuid().ToString();
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
  <s:Header>
    <a:Action s:mustUnderstand=""1"">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>
    <a:MessageID>urn:uuid:{msgId}</a:MessageID>
    <a:To s:mustUnderstand=""1"">urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>
  </s:Header>
  <s:Body>
    <d:Probe>
      <d:Types>dn:NetworkVideoTransmitter</d:Types>
    </d:Probe>
  </s:Body>
</s:Envelope>";
    }

    public static IPAddress CalculateBroadcastAddress(IPAddress ip, string? subnetMask)
    {
        if (string.IsNullOrWhiteSpace(subnetMask) || !IPAddress.TryParse(subnetMask, out var mask))
        {
            mask = IPAddress.Parse("255.255.255.0");
        }

        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != maskBytes.Length)
        {
            return IPAddress.Broadcast;
        }

        var broadcastBytes = new byte[ipBytes.Length];
        for (int i = 0; i < ipBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }
}
