using System.Diagnostics;
using System.Text;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Demo;
using CamScope.Streaming.Rendering;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming.Sessions;

/// <summary>
/// Dedicated streaming session for Demo Mode cameras.
/// Plays authentic, continuously looping 720p H.264 CCTV video footage via an uncompressed BMP pipe,
/// with measured FPS and graceful fallback to procedural generation if FFmpeg or assets are unavailable.
/// </summary>
public sealed class DemoVideoPlaybackSession : IStreamingSession
{
    private readonly CameraDevice _device;
    private readonly DemoCameraAssetMapping _mapping;
    private readonly string? _explicitUsername;
    private readonly string? _explicitPassword;
    private readonly ICredentialStore? _credentialStore;
    private readonly ILogger<DemoVideoPlaybackSession> _logger;
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _streamingTask;
    private Process? _ffmpegProcess;
    private int _frameCount;
    private double _currentFps;
    private bool _isDisposed;

    public Guid SessionId { get; } = Guid.NewGuid();
    public CameraDevice Device => _device;
    public StreamState State { get; private set; } = StreamState.Idle;
    public int FrameCount => _frameCount;
    public double CurrentFps => _currentFps;
    public string? StreamResolution { get; private set; }
    public string? LastError { get; private set; }

    public event Action<byte[]>? FrameReceived;
    public event Action<StreamState, string?>? StateChanged;

    public DemoVideoPlaybackSession(
        CameraDevice device,
        DemoCameraAssetMapping mapping,
        string? username,
        string? password,
        ICredentialStore? credentialStore,
        ILogger<DemoVideoPlaybackSession> logger)
    {
        _device = device;
        _mapping = mapping;
        _explicitUsername = username;
        _explicitPassword = password;
        _credentialStore = credentialStore;
        _logger = logger;
        StreamResolution = mapping.Resolution;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startStopLock.WaitAsync(ct);
        try
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(DemoVideoPlaybackSession));

            if (State == StreamState.Streaming || State == StreamState.Connecting)
                return;

            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TransitionState(StreamState.Connecting, "Initializing demo video feed...");

            var token = _sessionCts.Token;
            _streamingTask = Task.Run(() => StreamSupervisorLoopAsync(token), CancellationToken.None);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _startStopLock.WaitAsync(CancellationToken.None);
        try
        {
            if (State == StreamState.Stopped || State == StreamState.Idle)
                return;

            TransitionState(StreamState.Stopped, "Demo stream stopped");

            if (_sessionCts != null)
            {
                _sessionCts.Cancel();
                KillFfmpegProcess();

                if (_streamingTask != null)
                {
                    try
                    {
                        await _streamingTask.WaitAsync(TimeSpan.FromSeconds(3), ct);
                    }
                    catch
                    {
                        // Ignore cancellation or task timeout
                    }
                }

                _sessionCts.Dispose();
                _sessionCts = null;
            }

            TransitionState(StreamState.Idle, null);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task StreamSupervisorLoopAsync(CancellationToken ct)
    {
        // Check authentication requirement for unauthenticated cameras (e.g. Camera 4 Thermal)
        if (_device.AuthState == AuthenticationState.Unauthenticated)
        {
            var isAuthed = await CheckIsAuthenticatedAsync(ct);
            if (!isAuthed)
            {
                var authError = "Authentication required. Camera is unauthenticated — please provide ONVIF credentials to access live feed.";
                _logger.LogWarning("[DEMO] Stream blocked for {Camera}: {Reason}", _device.DisplayName, authError);
                TransitionState(StreamState.Error, authError);
                return;
            }
        }

        var videoPath = ResolveVideoFilePath(_mapping.VideoFileName);
        var ffmpegPath = RtspStreamSession.FindFfmpegPath();

        if (videoPath != null && File.Exists(videoPath) && ffmpegPath != null)
        {
            try
            {
                _logger.LogInformation("[DEMO] Starting realistic video loop for {Camera} using {VideoPath}", _device.DisplayName, videoPath);
                await StreamFfmpegVideoLoopAsync(ffmpegPath, videoPath, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DEMO] Video loop failed for {Camera}, falling back to synthetic generator", _device.DisplayName);
            }
        }

        // Graceful fallback to synthetic frame generator if video asset or ffmpeg is unavailable
        await RunSyntheticFallbackLoopAsync(ct);
    }

    private async Task<bool> CheckIsAuthenticatedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_explicitUsername) && !string.IsNullOrWhiteSpace(_explicitPassword))
        {
            return true;
        }

        if (_credentialStore != null)
        {
            try
            {
                var creds = await _credentialStore.GetCredentialsAsync(_device.EndpointId, ct);
                if (creds.HasValue && !string.IsNullOrWhiteSpace(creds.Value.Username) && !string.IsNullOrWhiteSpace(creds.Value.Password))
                {
                    return true;
                }
            }
            catch
            {
                // Fall through to unauthenticated
            }
        }

        return false;
    }

    private async Task StreamFfmpegVideoLoopAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");
        // Real-time playback speed
        psi.ArgumentList.Add("-re");
        // Loop continuously without stopping
        psi.ArgumentList.Add("-stream_loop");
        psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        // Output format image2pipe BMP
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec");
        psi.ArgumentList.Add("bmp");
        psi.ArgumentList.Add("-");

        using var process = new Process { StartInfo = psi };
        _ffmpegProcess = process;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to launch FFmpeg media pipeline process for demo video.");
        }

        var stderrSb = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    stderrSb.AppendLine(line);
                }
            }
            catch
            {
                // Ignore stderr read issues on shutdown
            }
        }, CancellationToken.None);

        var stdout = process.StandardOutput.BaseStream;
        var headerBuffer = new byte[54];
        var fpsStopwatch = Stopwatch.StartNew();
        int framesSinceLastFps = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int headerRead = 0;
                while (headerRead < 54)
                {
                    int read = await stdout.ReadAsync(headerBuffer.AsMemory(headerRead, 54 - headerRead), ct);
                    if (read == 0)
                    {
                        if (process.HasExited && process.ExitCode != 0)
                        {
                            var err = stderrSb.ToString().Trim();
                            throw new InvalidOperationException(
                                $"FFmpeg demo playback exited with code {process.ExitCode}: {err}");
                        }
                        return;
                    }
                    headerRead += read;
                }

                if (headerBuffer[0] != 0x42 || headerBuffer[1] != 0x4D) // 'BM'
                {
                    throw new InvalidOperationException("Invalid BMP magic header received from demo media stream.");
                }

                int totalFileSize = BitConverter.ToInt32(headerBuffer, 2);
                int width = BitConverter.ToInt32(headerBuffer, 18);
                int height = Math.Abs(BitConverter.ToInt32(headerBuffer, 22));

                if (totalFileSize < 54 || totalFileSize > 50_000_000)
                {
                    throw new InvalidOperationException($"Invalid frame size in BMP stream: {totalFileSize} bytes");
                }

                StreamResolution = $"{width}x{height}";

                int payloadSize = totalFileSize - 54;
                var frameBuffer = new byte[totalFileSize];
                Buffer.BlockCopy(headerBuffer, 0, frameBuffer, 0, 54);

                int payloadRead = 0;
                while (payloadRead < payloadSize)
                {
                    int read = await stdout.ReadAsync(frameBuffer.AsMemory(54 + payloadRead, payloadSize - payloadRead), ct);
                    if (read == 0)
                    {
                        return;
                    }
                    payloadRead += read;
                }

                _frameCount++;
                framesSinceLastFps++;

                if (State != StreamState.Streaming)
                {
                    TransitionState(StreamState.Streaming, null);
                }

                FrameReceived?.Invoke(frameBuffer);

                if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = framesSinceLastFps * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                    framesSinceLastFps = 0;
                    fpsStopwatch.Restart();
                }
            }
        }
        finally
        {
            KillFfmpegProcess();
            _ffmpegProcess = null;
        }
    }

    private async Task RunSyntheticFallbackLoopAsync(CancellationToken ct)
    {
        const int targetFps = 25;
        const int frameIntervalMs = 1000 / targetFps;

        var fpsStopwatch = Stopwatch.StartNew();
        int framesSinceLastFpsCheck = 0;

        try
        {
            await Task.Delay(100, ct);
            TransitionState(StreamState.Streaming, null);
            _logger.LogInformation("[DEMO] Fallback synthetic streaming active for {Camera}", _device.DisplayName);

            while (!ct.IsCancellationRequested)
            {
                var frameStartTime = Stopwatch.GetTimestamp();

                var frameBytes = SyntheticFrameGenerator.GenerateBmpFrame(
                    _device.DisplayName,
                    _device.Host,
                    _frameCount);

                _frameCount++;
                framesSinceLastFpsCheck++;

                FrameReceived?.Invoke(frameBytes);

                if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = framesSinceLastFpsCheck * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                    framesSinceLastFpsCheck = 0;
                    fpsStopwatch.Restart();
                }

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
            _logger.LogError(ex, "[DEMO] Fallback streaming error for {Camera}", _device.DisplayName);
            TransitionState(StreamState.Error, ex.Message);
        }
    }

    private void KillFfmpegProcess()
    {
        if (_ffmpegProcess == null) return;
        try
        {
            if (!_ffmpegProcess.HasExited)
            {
                _ffmpegProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill exceptions if process exited
        }
    }

    public static string? ResolveVideoFilePath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "videos", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "videos", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "CamScope.App", "Assets", "videos", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "CamScope.App", "Assets", "videos", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "videos", fileName)
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    public static string? ResolvePosterFilePath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "images", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "images", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "CamScope.App", "Assets", "images", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "CamScope.App", "Assets", "images", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "images", fileName)
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
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

        FrameReceived = null;
        StateChanged = null;

        await StopAsync(CancellationToken.None);
        _startStopLock.Dispose();
    }
}
