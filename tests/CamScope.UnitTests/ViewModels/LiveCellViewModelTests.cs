using System.Collections.ObjectModel;
using CamScope.App.ViewModels;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class LiveCellViewModelTests
{
    [Fact]
    public void Commands_AreGeneratedByMvvmToolkit()
    {
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var cameras = new ObservableCollection<CameraDevice>();
        var cell = new LiveCellViewModel(streamingService, cameras);

        Assert.NotNull(cell.StartCommand);
        Assert.NotNull(cell.StopCommand);
    }

    [Fact]
    public async Task StartAsync_WhenDeviceUnauthenticated_SetsErrorMessage()
    {
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var cameras = new ObservableCollection<CameraDevice>();
        var cell = new LiveCellViewModel(streamingService, cameras);

        var device = new CameraDevice
        {
            DisplayName = "Unauth Camera",
            Host = "192.168.1.50",
            AuthState = AuthenticationState.Unauthenticated
        };
        cell.Device = device;

        await cell.StartCommand.ExecuteAsync(null);

        Assert.False(cell.IsStreaming);
        Assert.NotNull(cell.ErrorMessage);
        Assert.Contains("authenticated", cell.ErrorMessage);
    }

    [Fact]
    public void ChangingDevice_UpdatesStatusText()
    {
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var cameras = new ObservableCollection<CameraDevice>();
        var cell = new LiveCellViewModel(streamingService, cameras);

        var device = new CameraDevice
        {
            DisplayName = "Camera 1",
            AuthState = AuthenticationState.Authenticated
        };

        cell.Device = device;
        Assert.Equal("Ready to stream", cell.StatusText);

        cell.Device = null;
        Assert.Equal("Idle - Select an authenticated camera", cell.StatusText);
    }

    [Fact]
    public void ChangingDevice_ToDemoDevice_SetsStatusText()
    {
        var streamingService = new StreamingService(NullLoggerFactory.Instance);
        var cameras = new ObservableCollection<CameraDevice>();
        var cell = new LiveCellViewModel(streamingService, cameras);

        var device = new CameraDevice
        {
            DisplayName = "[DEMO] Front Entrance Dome",
            Host = "192.168.1.101",
            AuthState = AuthenticationState.Authenticated,
            IsDemoDevice = true
        };

        cell.Device = device;
        Assert.Equal("Sample preview active - Click Start to stream", cell.StatusText);
        if (Avalonia.Application.Current != null)
        {
            Assert.NotNull(cell.CurrentBitmap);
        }
    }
}

