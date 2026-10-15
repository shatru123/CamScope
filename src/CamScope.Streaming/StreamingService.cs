using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming;

public sealed class StreamingService : IStreamingService
{
    private readonly ILoggerFactory _loggerFactory;

    public StreamingService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IStreamingSession CreateSession(CameraDevice device, string? username = null, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var logger = _loggerFactory.CreateLogger<RtspStreamSession>();
        return new RtspStreamSession(device, username, password, logger);
    }
}
