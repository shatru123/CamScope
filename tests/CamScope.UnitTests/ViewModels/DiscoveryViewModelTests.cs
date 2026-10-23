using System.Collections.ObjectModel;
using CamScope.App.ViewModels;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class DiscoveryViewModelTests
{
    private sealed class DummyDiscoveryEngine : INetworkDiscoveryEngine
    {
        public List<CameraDevice> DevicesToReturn { get; } = [];
        public DiscoveryStatus Status { get; set; } = DiscoveryStatus.Idle;

        public Task<IReadOnlyList<CameraDevice>> DiscoverAsync(
            NetworkInterfaceInfo iface,
            CancellationToken ct = default,
            IProgress<DiscoveryProgressReport>? progress = null)
        {
            progress?.Report(new DiscoveryProgressReport { Percentage = 100, CurrentStep = "Done" });
            return Task.FromResult<IReadOnlyList<CameraDevice>>(DevicesToReturn);
        }

        public void Cancel() { }
    }

    private sealed class DummyInterfaceProvider : INetworkInterfaceProvider
    {
        public List<NetworkInterfaceInfo> EligibleInterfaces { get; } = [];
        public List<NetworkInterfaceInfo> AllInterfaces { get; } = [];
        public event Action? InterfacesChanged;

        public IReadOnlyList<NetworkInterfaceInfo> GetEligibleInterfaces(bool includeLoopback = false) => EligibleInterfaces;
        public IReadOnlyList<NetworkInterfaceInfo> GetAllInterfaces() => AllInterfaces;
        public void TriggerChange() => InterfacesChanged?.Invoke();
        public void Dispose() { }
    }

    private sealed class InMemoryDeviceRepository : IDeviceRepository
    {
        public List<CameraDevice> Devices { get; } = [];

        public Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CameraDevice>>(Devices);

        public Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id));

        public Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default)
            => Task.FromResult(Devices.FirstOrDefault(d => d.EndpointId == endpointId));

        public Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default)
        {
            var idx = Devices.FindIndex(d => d.EndpointId == device.EndpointId);
            if (idx >= 0) Devices[idx] = device;
            else Devices.Add(device);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            Devices.RemoveAll(d => d.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken ct = default)
        {
            Devices.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class DummyAuditService : IAuditService
    {
        public Task LogAsync(AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int count = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>([]);
        public Task ClearLogsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyOnvifClient : IOnvifClient
    {
        public Task<bool> ProbeReachableAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
        public Task<DeviceInformationResult> GetDeviceInformationAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult(new DeviceInformationResult("Vendor", "Model", "1.0", "123", "HW1"));
        public Task<DeviceCapabilities> GetCapabilitiesAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult(new DeviceCapabilities());
        public Task<string?> GetStreamUriAsync(string serviceAddress, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult<string?>("rtsp://127.0.0.1/live");
        public Task<IReadOnlyList<RecordingSegment>> FindRecordingsAsync(string serviceAddress, DateTime startUtc, DateTime endUtc, string? username, string? password, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RecordingSegment>>([]);
        public Task<CameraConnectionTestResult> TestConnectionAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(new CameraConnectionTestResult(true, 10.0, true, true, null));
    }

    private sealed class DummyCredentialStore : ICredentialStore
    {
        public Task SaveCredentialsAsync(string endpointId, string username, string password, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(string? Username, string? Password)?> GetCredentialsAsync(string endpointId, CancellationToken ct = default) => Task.FromResult<(string?, string?)?>(null);
        public Task DeleteCredentialsAsync(string endpointId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void SelectingInterface_ComputesSubnetCidrAutomatically()
    {
        var realEngine = new DummyDiscoveryEngine();
        var demoEngine = new DummyDiscoveryEngine();
        var ifaceProvider = new DummyInterfaceProvider();
        var repo = new InMemoryDeviceRepository();
        var audit = new DummyAuditService();

        var vm = new DiscoveryViewModel(
            realEngine, demoEngine, ifaceProvider, repo, audit, () => false);

        var iface = new NetworkInterfaceInfo
        {
            Id = "eth0",
            Name = "eth0",
            IPv4Address = "10.50.100.45",
            SubnetMask = "255.255.255.0"
        };

        vm.SelectedInterface = iface;

        Assert.Equal("10.50.100.0/24", vm.SubnetCidr);
    }

    [Fact]
    public void VlanDisclosureText_ContainsClearNoticeRegardingMulticastAndClientIsolation()
    {
        var realEngine = new DummyDiscoveryEngine();
        var demoEngine = new DummyDiscoveryEngine();
        var ifaceProvider = new DummyInterfaceProvider();
        var repo = new InMemoryDeviceRepository();
        var audit = new DummyAuditService();

        var vm = new DiscoveryViewModel(
            realEngine, demoEngine, ifaceProvider, repo, audit, () => false);

        Assert.Contains("VLAN", vm.VlanDisclosureText);
        Assert.Contains("Isolation", vm.VlanDisclosureText);
        Assert.Contains("WS-Discovery", vm.VlanDisclosureText);
    }

    [Fact]
    public async Task AddManualCamera_DirectRtsp_SavesDeviceAndUpdatesList()
    {
        var realEngine = new DummyDiscoveryEngine();
        var demoEngine = new DummyDiscoveryEngine();
        var ifaceProvider = new DummyInterfaceProvider();
        var repo = new InMemoryDeviceRepository();
        var audit = new DummyAuditService();
        var onvifClient = new DummyOnvifClient();
        var credStore = new DummyCredentialStore();
        var deviceService = new DeviceManagementService(
            repo, credStore, onvifClient, audit, NullLogger<DeviceManagementService>.Instance);

        var vm = new DiscoveryViewModel(
            realEngine, demoEngine, ifaceProvider, repo, audit, () => false,
            deviceService: deviceService);

        vm.ManualProtocolIndex = 1; // RTSP
        vm.ManualCameraName = "Yard Cam";
        vm.ManualHostOrUrl = "rtsp://192.168.1.180:554/h264";
        vm.ManualUsername = "admin";
        vm.ManualPassword = "pass";
        vm.ManualLocation = "Backyard";
        vm.ManualRtspTransport = "udp";

        await vm.AddManualCameraCommand.ExecuteAsync(null);

        Assert.Contains("Successfully added", vm.ManualAddStatus);
        Assert.Single(vm.DiscoveredDevices);
        Assert.Equal("Yard Cam", vm.DiscoveredDevices[0].DisplayName);
        Assert.Equal("udp", vm.DiscoveredDevices[0].RtspTransport);
    }
}
