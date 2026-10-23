using CamScope.App.ViewModels;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class LiveMonitoringViewModelTests
{
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

    [Fact]
    public async Task LoadCamerasAsync_InDemoMode_SeedsCamerasAndAssignsCells()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true);

        Assert.Equal(4, vm.Cells.Count);
        Assert.Null(vm.Cells[0].Device);

        await vm.LoadCamerasAsync();

        // Must seed demo devices and populate authenticated cameras
        Assert.NotEmpty(repo.Devices);
        Assert.NotEmpty(vm.AuthenticatedCameras);

        // Cells 0-3 must be assigned to authenticated demo cameras
        for (int i = 0; i < 4; i++)
        {
            Assert.NotNull(vm.Cells[i].Device);
            Assert.Equal(AuthenticationState.Authenticated, vm.Cells[i].Device!.AuthState);
            Assert.True(vm.Cells[i].Device!.IsDemoDevice);
        }

        // Clean up
        await vm.DisposeAsync();
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(9, 3, 3)]
    public async Task SetLayout_AdjustsGridRowsColumnsAndCellCount(int targetCells, int expectedRows, int expectedCols)
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => true);

        vm.SetLayout(targetCells);

        Assert.Equal(targetCells, vm.Cells.Count);
        Assert.Equal(expectedRows, vm.GridRows);
        Assert.Equal(expectedCols, vm.GridColumns);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StopAllAsync_StopsAllStreamingCells()
    {
        var repo = new InMemoryDeviceRepository();
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var vm = new LiveMonitoringViewModel(repo, streamingService, () => false);

        await vm.LoadCamerasAsync();
        await vm.StopAllAsync();

        Assert.All(vm.Cells, cell => Assert.False(cell.IsStreaming));
        await vm.DisposeAsync();
    }
}
