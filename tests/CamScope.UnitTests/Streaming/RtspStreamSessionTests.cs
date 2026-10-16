using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Streaming.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Streaming;

public class RtspStreamSessionTests
{
    [Fact]
    public async Task RealDevice_WithoutStreamUri_EntersErrorStateAndNeverGeneratesFrames()
    {
        var device = new CameraDevice
        {
            DisplayName = "Real Physical Camera",
            Host = "192.168.1.200",
            IsDemoDevice = false,
            RtspStreamUri = null // Unconfigured stream URI
        };

        var session = new RtspStreamSession(
            device,
            "admin",
            "secret",
            NullLogger<RtspStreamSession>.Instance);

        int frameCount = 0;
        session.FrameReceived += _ => Interlocked.Increment(ref frameCount);

        await session.StartAsync();

        // Wait briefly for supervisor loop to evaluate
        await Task.Delay(200);

        Assert.Equal(StreamState.Error, session.State);
        Assert.NotNull(session.LastError);
        Assert.Contains("RTSP stream URI", session.LastError);
        Assert.Equal(0, frameCount); // MUST NEVER output synthetic frames in real device mode

        await session.StopAsync();
    }

    [Fact]
    public async Task DemoDevice_ProducesSyntheticFramesAndStreamingState()
    {
        var device = new CameraDevice
        {
            DisplayName = "Evaluation Camera",
            Host = "192.168.1.10",
            IsDemoDevice = true
        };

        var session = new RtspStreamSession(
            device,
            null,
            null,
            NullLogger<RtspStreamSession>.Instance);

        var frameReceivedTcs = new TaskCompletionSource<byte[]>();
        session.FrameReceived += bytes => frameReceivedTcs.TrySetResult(bytes);

        await session.StartAsync();

        var frame = await Task.WhenAny(frameReceivedTcs.Task, Task.Delay(3000));
        Assert.Same(frameReceivedTcs.Task, frame);

        var frameBytes = await frameReceivedTcs.Task;
        Assert.NotNull(frameBytes);
        Assert.True(frameBytes.Length > 54);
        Assert.Equal(0x42, frameBytes[0]); // 'B'
        Assert.Equal(0x4D, frameBytes[1]); // 'M'

        Assert.Equal(StreamState.Streaming, session.State);
        Assert.True(session.FrameCount > 0);

        await session.StopAsync();
        Assert.Equal(StreamState.Idle, session.State);
    }

    [Theory]
    [InlineData("rtsp://192.168.1.50/stream1", "admin", "pass123", "rtsp://admin:pass123@192.168.1.50/stream1")]
    [InlineData("rtsp://192.168.1.50:554/h264", "operator", "p@ss#word", "rtsp://operator:p%40ss%23word@192.168.1.50:554/h264")]
    [InlineData("rtsp://custom:cred@192.168.1.50/live", "admin", "ignored", "rtsp://custom:cred@192.168.1.50/live")]
    [InlineData("rtsp://192.168.1.50/stream", null, null, "rtsp://192.168.1.50/stream")]
    public void BuildAuthenticatedRtspUri_EmbedsCredentialsSafely(string rawUri, string? user, string? pass, string expected)
    {
        var actual = RtspStreamSession.BuildAuthenticatedRtspUri(rawUri, user, pass);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)] // Capped at 30s
    [InlineData(10, 30)]
    public void CalculateBackoffDelaySeconds_CappedAtMax(int retry, int expected)
    {
        var delay = RtspStreamSession.CalculateBackoffDelaySeconds(retry, 30);
        Assert.Equal(expected, delay);
    }

    [Fact]
    public async Task StartAsync_ConcurrentInvocations_DoesNotThrow()
    {
        var device = new CameraDevice
        {
            DisplayName = "Demo Device",
            IsDemoDevice = true
        };

        var session = new RtspStreamSession(
            device,
            null,
            null,
            NullLogger<RtspStreamSession>.Instance);

        var tasks = Enumerable.Range(0, 5).Select(_ => session.StartAsync()).ToArray();
        await Task.WhenAll(tasks);

        Assert.True(session.State == StreamState.Connecting || session.State == StreamState.Streaming);

        await session.StopAsync();
        Assert.Equal(StreamState.Idle, session.State);
    }
}
