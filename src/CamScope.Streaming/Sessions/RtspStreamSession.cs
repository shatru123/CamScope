using System.Diagnostics;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Rendering;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming.Sessions;

public sealed class RtspStreamSession : IStreamingSession
{
    private readonly CameraDevice _device;
    private readonly string? _username;
    private readonly string? _password;
    private readonly ILogger<RtspStreamSession> _logger;

    private CancellationTokenSource? _sessionCts;
    private Task? _streamingTask;
    private int _frameCount;
    private double _currentFps;
    private int _retryCount;
    private bool _isDisposed;

    public Guid SessionId { get; } = Guid.NewGuid();
    public CameraDevice Device => _device;
    public StreamState State { get; private set; } = StreamState.Idle;
    public int FrameCount => _frameCount;
    public double CurrentFps => _currentFps;
    public string? StreamResolution { get; private set; } = "1920x1080";
    public string? LastError { get; private set; }

    public event Action<byte[]>? FrameReceived;
    public event Action<StreamState, string?>? StateChanged;

    public RtspStreamSession(
        CameraDevice device,
        string? username,
        string? password,
        ILogger<RtspStreamSession> logger)
    {
        _device = device;
        _username = username;
        _password = password;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (State == StreamState.Streaming || State == StreamState.Connecting)
            return Task.CompletedTask;

        _sessionCts = new CancellationTokenSource();
        TransitionState(StreamState.Connecting, "Establishing stream session...");

        _streamingTask = Task.Run(() => StreamLoopAsync(_sessionCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (State == StreamState.Stopped || State == StreamState.Idle)
            return;

        TransitionState(StreamState.Stopped, "Stream manually stopped by user");

        if (_sessionCts != null)
        {
            _sessionCts.Cancel();
            if (_streamingTask != null)
            {
                try
                {
                    await Task.WhenAny(_streamingTask, Task.Delay(1000, ct));
                }
                catch
                {
                    // Ignore task cancellation
                }
            }
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        TransitionState(StreamState.Idle, null);
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        const int targetFps = 20;
        const int frameIntervalMs = 1000 / targetFps;

        var fpsStopwatch = Stopwatch.StartNew();
        int framesSinceLastFpsCheck = 0;

        try
        {
            // Simulate connection handshake (authenticating RTSP DESCRIBE/SETUP/PLAY)
            await Task.Delay(300, ct);

            TransitionState(StreamState.Streaming, null);
            _logger.LogInformation("Streaming session {SessionId} active for {Camera}", SessionId, _device.DisplayName);

            while (!ct.IsCancellationRequested)
            {
                var frameStartTime = Stopwatch.GetTimestamp();

                // Generate surveillance frame
                var frameBytes = SyntheticFrameGenerator.GenerateBmpFrame(
                    _device.DisplayName,
                    _device.Host,
                    _frameCount);

                _frameCount++;
                framesSinceLastFpsCheck++;

                // Trigger event
                FrameReceived?.Invoke(frameBytes);

                // Update FPS every second
                if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = framesSinceLastFpsCheck * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                    framesSinceLastFpsCheck = 0;
                    fpsStopwatch.Restart();
                }

                // Throttle to steady target frame rate
                var elapsedMs = (int)Stopwatch.GetElapsedTime(frameStartTime).TotalMilliseconds;
                var delay = Math.Max(1, frameIntervalMs - elapsedMs);
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming error in session {SessionId} for {Camera}", SessionId, _device.DisplayName);
            LastError = ex.Message;
            await HandleReconnectAsync(ct);
        }
    }

    private async Task HandleReconnectAsync(CancellationToken ct)
    {
        const int maxRetries = 5;

        while (_retryCount < maxRetries && !ct.IsCancellationRequested)
        {
            _retryCount++;
            var backoffSeconds = Math.Min(30, (int)Math.Pow(2, _retryCount - 1));

            TransitionState(
                StreamState.Reconnecting,
                $"Stream disconnected. Reconnecting in {backoffSeconds}s (attempt {_retryCount}/{maxRetries})...");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
                TransitionState(StreamState.Connecting, "Re-establishing connection...");
                _retryCount = 0;
                await StreamLoopAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        TransitionState(StreamState.Error, $"Connection lost after {maxRetries} failed reconnection attempts.");
    }

    public static int CalculateBackoffDelaySeconds(int retryCount, int maxDelay = 30)
    {
        if (retryCount <= 0) return 1;
        var exp = Math.Pow(2, retryCount - 1);
        return (int)Math.Min(maxDelay, exp);
    }

    private void TransitionState(StreamState newState, string? message)
    {
        State = newState;
        LastError = newState == StreamState.Error ? message : null;
        StateChanged?.Invoke(newState, message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync(CancellationToken.None);
        _sessionCts?.Dispose();
    }
}
