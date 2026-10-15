using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;

namespace CamScope.Discovery.Engine;

public sealed class DemoDiscoveryEngine : INetworkDiscoveryEngine
{
    private CancellationTokenSource? _activeCts;
    public DiscoveryStatus Status { get; private set; } = DiscoveryStatus.Idle;

    public void Cancel()
    {
        _activeCts?.Cancel();
    }

    public async Task<IReadOnlyList<CameraDevice>> DiscoverAsync(
        NetworkInterfaceInfo iface,
        CancellationToken ct = default,
        IProgress<DiscoveryProgressReport>? progress = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts = linkedCts;

        var demoDevices = DemoDataGenerator.CreateDemoDevices();
        var discovered = new List<CameraDevice>();

        try
        {
            Status = DiscoveryStatus.BindingInterface;
            progress?.Report(new DiscoveryProgressReport
            {
                Status = Status,
                Percentage = 10,
                CurrentStep = $"[DEMO] Initializing simulated discovery on {iface.Name}...",
                DevicesFound = 0
            });
            await Task.Delay(400, linkedCts.Token);

            Status = DiscoveryStatus.SendingProbes;
            progress?.Report(new DiscoveryProgressReport
            {
                Status = Status,
                Percentage = 25,
                CurrentStep = "[DEMO] Simulating multicast ONVIF probe...",
                DevicesFound = 0
            });
            await Task.Delay(500, linkedCts.Token);

            Status = DiscoveryStatus.AwaitingResponses;
            progress?.Report(new DiscoveryProgressReport
            {
                Status = Status,
                Percentage = 40,
                CurrentStep = "[DEMO] Simulating camera announcements...",
                DevicesFound = 0
            });

            for (int i = 0; i < demoDevices.Count; i++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                await Task.Delay(350, linkedCts.Token);

                var device = demoDevices[i];
                discovered.Add(device);

                var percent = 40 + (int)((i + 1.0) / demoDevices.Count * 50);
                progress?.Report(new DiscoveryProgressReport
                {
                    Status = DiscoveryStatus.ProcessingDevices,
                    Percentage = percent,
                    CurrentStep = $"[DEMO] Discovered: {device.DisplayName} ({device.Host})",
                    DevicesFound = discovered.Count,
                    LatestDevice = device
                });
            }

            Status = DiscoveryStatus.Completed;
            progress?.Report(new DiscoveryProgressReport
            {
                Status = Status,
                Percentage = 100,
                CurrentStep = "[DEMO] Discovery complete. 5 simulated cameras discovered.",
                DevicesFound = discovered.Count
            });

            return discovered;
        }
        catch (OperationCanceledException)
        {
            Status = DiscoveryStatus.Cancelled;
            progress?.Report(new DiscoveryProgressReport
            {
                Status = Status,
                Percentage = 100,
                CurrentStep = "[DEMO] Discovery was cancelled by user.",
                DevicesFound = discovered.Count
            });
            return discovered;
        }
        finally
        {
            _activeCts = null;
        }
    }
}
