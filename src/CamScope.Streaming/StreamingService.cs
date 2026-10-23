using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Demo;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming;

public sealed class StreamingService : IStreamingService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICredentialStore? _credentialStore;
    private readonly IDemoVideoPlaybackService? _demoPlaybackService;

    public StreamingService(
        ILoggerFactory loggerFactory,
        ICredentialStore? credentialStore = null,
        IDemoVideoPlaybackService? demoPlaybackService = null)
    {
        _loggerFactory = loggerFactory;
        _credentialStore = credentialStore;
        _demoPlaybackService = demoPlaybackService;
    }

    public IStreamingSession CreateSession(CameraDevice device, string? username = null, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (_demoPlaybackService != null && _demoPlaybackService.IsDemoDevice(device))
        {
            var mapping = _demoPlaybackService.GetMapping(device)
                ?? DemoCameraAssetCatalog.ResolveForDevice(device);
            var demoLogger = _loggerFactory.CreateLogger<DemoVideoPlaybackSession>();
            return new DemoVideoPlaybackSession(device, mapping, username, password, _credentialStore, demoLogger);
        }

        var logger = _loggerFactory.CreateLogger<RtspStreamSession>();
        return new RtspStreamSession(device, username, password, _credentialStore, logger);
    }
}
