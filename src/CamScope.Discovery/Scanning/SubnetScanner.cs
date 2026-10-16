using System.Net;
using System.Net.Sockets;
using System.Text;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Discovery.Deduplication;
using CamScope.Discovery.Engine;
using CamScope.Discovery.Parsing;
using Microsoft.Extensions.Logging;

namespace CamScope.Discovery.Scanning;

public sealed class SubnetScanner : ISubnetScanner
{
    private const int MaxAllowedHosts = 512;
    private const int BufferSize = 65536;

    private readonly ILogger<SubnetScanner> _logger;
    private readonly DeviceDeduplicator _deduplicator = new();

    public SubnetScanner(ILogger<SubnetScanner> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CameraDevice>> ScanSubnetAsync(
        SubnetScanRequest request,
        IProgress<DiscoveryProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetInterface == null ||
            string.IsNullOrWhiteSpace(request.TargetInterface.IPv4Address) ||
            !IPAddress.TryParse(request.TargetInterface.IPv4Address, out var localIp))
        {
            throw new ArgumentException("Target interface must have a valid IPv4 address.", nameof(request));
        }

        var targetHosts = CalculateHostIps(request.CidrOrSubnet, MaxAllowedHosts);
        if (targetHosts.Count == 0)
        {
            throw new ArgumentException($"No valid host addresses found in CIDR '{request.CidrOrSubnet}'.", nameof(request));
        }

        _deduplicator.Clear();
        _logger.LogInformation("Starting directed subnet scan on interface {Interface} ({LocalIp}) across {Count} hosts in {Subnet}",
            request.TargetInterface.Name, localIp, targetHosts.Count, request.CidrOrSubnet);

        ReportProgress(progress, DiscoveryStatus.BindingInterface, 5,
            $"Binding socket to {request.TargetInterface.Name} ({localIp})...", 0);

        Socket? socket = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(localIp, 0));

            var receiveBuffer = new byte[BufferSize];
            var receivingCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

            // Background receiver task
            var receiverTask = Task.Run(async () =>
            {
                while (!receivingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var receiveResult = await socket.ReceiveFromAsync(
                            receiveBuffer,
                            SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0),
                            receivingCts.Token).ConfigureAwait(false);

                        if (receiveResult.ReceivedBytes > 0)
                        {
                            var responseXml = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.ReceivedBytes);
                            ProcessResponse(responseXml, request, progress);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogDebug(ex, "Socket receive terminated or closed during subnet scan");
                        break;
                    }
                }
            }, receivingCts.Token);

            // Probe payload
            var probeMessage = WsDiscoveryEngine.BuildProbeMessage();
            var probeBytes = Encoding.UTF8.GetBytes(probeMessage);
            int probePort = request.ProbePort > 0 ? request.ProbePort : 3702;

            ReportProgress(progress, DiscoveryStatus.SendingProbes, 15,
                $"Sending directed WS-Discovery probes to {targetHosts.Count} hosts...", 0);

            // Bounded concurrency sending
            int maxConcurrency = Math.Clamp(request.MaxConcurrency, 1, 32);
            using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var sendTasks = new List<Task>();

            int sentCount = 0;
            for (int i = 0; i < targetHosts.Count; i++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var targetHost = targetHosts[i];
                await throttle.WaitAsync(linkedCts.Token).ConfigureAwait(false);

                sendTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var endpoint = new IPEndPoint(targetHost, probePort);
                        await socket.SendToAsync(probeBytes, SocketFlags.None, endpoint, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogTrace(ex, "Failed to send directed probe to {Target}", targetHost);
                    }
                    finally
                    {
                        throttle.Release();
                        int current = Interlocked.Increment(ref sentCount);
                        int percent = 15 + (int)((current / (double)targetHosts.Count) * 65);
                        if (current % 10 == 0 || current == targetHosts.Count)
                        {
                            ReportProgress(progress, DiscoveryStatus.SendingProbes, percent,
                                $"Sent probe {current}/{targetHosts.Count} ({targetHost})...",
                                _deduplicator.GetAll().Count);
                        }
                    }
                }, linkedCts.Token));
            }

            await Task.WhenAll(sendTasks).ConfigureAwait(false);

            ReportProgress(progress, DiscoveryStatus.AwaitingResponses, 85,
                "Awaiting probe responses from subnet...", _deduplicator.GetAll().Count);

            // Await remaining window for responses
            var waitWindow = request.TimeoutPerHost > TimeSpan.Zero
                ? request.TimeoutPerHost
                : TimeSpan.FromMilliseconds(500);

            try
            {
                await Task.Delay(waitWindow, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }

            // Cancel receiver
            receivingCts.Cancel();
            try
            {
                await receiverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }

            var results = _deduplicator.GetAll();
            ReportProgress(progress, DiscoveryStatus.Completed, 100,
                $"Subnet scan completed. Found {results.Count} camera(s).", results.Count);

            return results;
        }
        catch (OperationCanceledException)
        {
            var results = _deduplicator.GetAll();
            ReportProgress(progress, DiscoveryStatus.Cancelled, 100,
                "Subnet scan was cancelled.", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subnet scan failed on interface {Interface} for CIDR {Subnet}",
                request.TargetInterface.Name, request.CidrOrSubnet);
            ReportProgress(progress, DiscoveryStatus.Failed, 100,
                $"Subnet scan failed: {ex.Message}", _deduplicator.GetAll().Count);
            throw;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private void ProcessResponse(
        string responseXml,
        SubnetScanRequest request,
        IProgress<DiscoveryProgressReport>? progress)
    {
        try
        {
            var devices = ProbeMatchParser.ParseProbeMatches(responseXml);
            foreach (var dev in devices)
            {
                if (!dev.Tags.Contains($"Interface:{request.TargetInterface.Name}"))
                {
                    dev.Tags.Add($"Interface:{request.TargetInterface.Name}");
                }

                var sourceTag = $"Source:SubnetScan:{request.CidrOrSubnet}";
                if (!dev.Tags.Contains(sourceTag))
                {
                    dev.Tags.Add(sourceTag);
                }

                var isNew = _deduplicator.TryAddOrUpdate(dev, out var current);
                if (isNew)
                {
                    _logger.LogInformation("Discovered device {DisplayName} at {Host}:{Port} via subnet scan {Subnet}",
                        current.DisplayName, current.Host, current.Port, request.CidrOrSubnet);

                    ReportProgress(
                        progress,
                        DiscoveryStatus.ProcessingDevices,
                        90,
                        $"Found: {current.DisplayName} ({current.Host})",
                        _deduplicator.GetAll().Count,
                        current);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse subnet discovery response packet");
        }
    }

    private static void ReportProgress(
        IProgress<DiscoveryProgressReport>? progress,
        DiscoveryStatus status,
        int percentage,
        string step,
        int devicesFound,
        CameraDevice? latest = null)
    {
        progress?.Report(new DiscoveryProgressReport
        {
            Status = status,
            Percentage = percentage,
            CurrentStep = step,
            DevicesFound = devicesFound,
            LatestDevice = latest
        });
    }

    public static List<IPAddress> CalculateHostIps(string cidrOrIp, int maxHosts = MaxAllowedHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cidrOrIp);
        cidrOrIp = cidrOrIp.Trim();

        if (!cidrOrIp.Contains('/'))
        {
            if (IPAddress.TryParse(cidrOrIp, out var singleIp) && singleIp.AddressFamily == AddressFamily.InterNetwork)
            {
                return [singleIp];
            }
            throw new ArgumentException($"Invalid IPv4 address: '{cidrOrIp}'", nameof(cidrOrIp));
        }

        var parts = cidrOrIp.Split('/');
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var baseIp) ||
            baseIp.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefixLength) ||
            prefixLength is < 8 or > 32)
        {
            throw new ArgumentException($"Invalid CIDR notation: '{cidrOrIp}'. Expected format like '192.168.1.0/24'.", nameof(cidrOrIp));
        }

        byte[] ipBytes = baseIp.GetAddressBytes();
        uint ipNum = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) | ((uint)ipBytes[2] << 8) | ipBytes[3];

        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        uint network = ipNum & mask;
        uint broadcast = network | ~mask;

        uint start = prefixLength >= 31 ? network : network + 1;
        uint end = prefixLength >= 31 ? broadcast : broadcast - 1;

        if (end < start)
        {
            return [baseIp];
        }

        long totalHosts = (long)end - start + 1;
        long hostCount = Math.Min(totalHosts, maxHosts);

        var list = new List<IPAddress>((int)hostCount);
        for (uint current = start; current <= end && list.Count < hostCount; current++)
        {
            byte[] bytes = [
                (byte)((current >> 24) & 0xFF),
                (byte)((current >> 16) & 0xFF),
                (byte)((current >> 8) & 0xFF),
                (byte)(current & 0xFF)
            ];
            list.Add(new IPAddress(bytes));
        }

        return list;
    }
}
