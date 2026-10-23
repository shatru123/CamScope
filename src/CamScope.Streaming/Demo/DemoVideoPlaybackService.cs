using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming.Demo;

/// <summary>
/// Service managing demo camera video asset resolution, mapping, and playback session creation.
/// </summary>
public sealed class DemoVideoPlaybackService : IDemoVideoPlaybackService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICredentialStore? _credentialStore;

    public DemoVideoPlaybackService(ILoggerFactory loggerFactory, ICredentialStore? credentialStore = null)
    {
        _loggerFactory = loggerFactory;
        _credentialStore = credentialStore;
    }

    public bool IsDemoDevice(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.IsDemoDevice || DemoCameraAssetCatalog.FindByEndpointId(device.EndpointId) != null;
    }

    public DemoCameraAssetMapping? GetMapping(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return DemoCameraAssetCatalog.ResolveForDevice(device);
    }

    public string? ResolveVideoPath(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var mapping = DemoCameraAssetCatalog.ResolveForDevice(device);
        return DemoVideoPlaybackSession.ResolveVideoFilePath(mapping.VideoFileName);
    }

    public string? ResolvePosterPath(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var mapping = DemoCameraAssetCatalog.ResolveForDevice(device);
        return DemoVideoPlaybackSession.ResolvePosterFilePath(mapping.PosterFileName);
    }

    public IStreamingSession CreateDemoSession(CameraDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var mapping = DemoCameraAssetCatalog.ResolveForDevice(device);
        var logger = _loggerFactory.CreateLogger<DemoVideoPlaybackSession>();
        return new DemoVideoPlaybackSession(device, mapping, null, null, _credentialStore, logger);
    }
}
