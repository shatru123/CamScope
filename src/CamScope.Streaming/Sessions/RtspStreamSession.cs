using System.Diagnostics;
using System.Text;
using CamScope.Application.Security;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Streaming.Rendering;
using Microsoft.Extensions.Logging;

namespace CamScope.Streaming.Sessions;

public sealed class RtspStreamSession : IStreamingSession
{
    private readonly CameraDevice _device;
    private readonly string? _explicitUsername;
    private readonly string? _explicitPassword;
    private readonly ICredentialStore? _credentialStore;
    private readonly ILogger<RtspStreamSession> _logger;
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
    public string? StreamResolution { get; private set; } = "1920x1080";
    public string? LastError { get; private set; }
    public int SocketTimeoutSeconds { get; set; } = 8;
    public string Transport => string.Equals(_device.RtspTransport, "udp", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";

    public event Action<byte[]>? FrameReceived;
    public event Action<StreamState, string?>? StateChanged;

    public RtspStreamSession(
        CameraDevice device,
        string? username,
        string? password,
        ILogger<RtspStreamSession> logger)
        : this(device, username, password, null, logger)
    {
    }

    public RtspStreamSession(
        CameraDevice device,
        string? username,
        string? password,
        ICredentialStore? credentialStore,
        ILogger<RtspStreamSession> logger)
    {
        _device = device;
        _explicitUsername = username;
        _explicitPassword = password;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startStopLock.WaitAsync(ct);
        try
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RtspStreamSession));

            if (State == StreamState.Streaming || State == StreamState.Connecting)
                return;

            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            TransitionState(StreamState.Connecting, "Establishing stream session...");

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

            TransitionState(StreamState.Stopped, "Stream stopped");

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
        if (_device.IsDemoDevice)
        {
            await RunDemoSyntheticLoopAsync(ct);
            return;
        }

        // Real Camera Device mode - strictly prohibited from generating synthetic frames
        await RunRealRtspSupervisorLoopAsync(ct);
    }

    private async Task RunDemoSyntheticLoopAsync(CancellationToken ct)
    {
        const int targetFps = 20;
        const int frameIntervalMs = 1000 / targetFps;

        var fpsStopwatch = Stopwatch.StartNew();
        int framesSinceLastFpsCheck = 0;

        try
        {
            await Task.Delay(200, ct);
            TransitionState(StreamState.Streaming, null);
            _logger.LogInformation("[DEMO] Synthetic streaming active for {Camera}", _device.DisplayName);

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
            _logger.LogError(ex, "[DEMO] Streaming error for {Camera}", _device.DisplayName);
            TransitionState(StreamState.Error, ex.Message);
        }
    }

    private async Task RunRealRtspSupervisorLoopAsync(CancellationToken ct)
    {
        var ffmpegPath = FindFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var msg = "FFmpeg media pipeline executable not found on system PATH. Install FFmpeg to enable live camera RTSP streaming.";
            _logger.LogError("{Message}", msg);
            TransitionState(StreamState.Error, msg);
            return;
        }

        if (string.IsNullOrWhiteSpace(_device.RtspStreamUri))
        {
            var msg = $"Device '{_device.DisplayName}' does not have an RTSP stream URI configured. Authenticate the camera via ONVIF to discover its RTSP media URI.";
            _logger.LogError("{Message}", msg);
            TransitionState(StreamState.Error, msg);
            return;
        }

        // Securely retrieve credentials if not supplied
        string? username = _explicitUsername;
        string? password = _explicitPassword;
        if (string.IsNullOrEmpty(username) && _credentialStore != null)
        {
            try
            {
                var creds = await _credentialStore.GetCredentialsAsync(_device.EndpointId, ct);
                if (creds.HasValue)
                {
                    username = creds.Value.Username;
                    password = creds.Value.Password;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve credentials from store for {Camera}", _device.DisplayName);
            }
        }

        var effectiveUri = BuildAuthenticatedRtspUri(_device.RtspStreamUri, username, password);
        var safeDisplayUri = SecretRedactor.Redact(effectiveUri);
        _logger.LogInformation("Connecting RTSP session {SessionId} to {Uri}", SessionId, safeDisplayUri);

        const int maxRetries = 5;
        int retryCount = 0;

        while (!ct.IsCancellationRequested && retryCount <= maxRetries)
        {
            try
            {
                await StreamFfmpegFramesAsync(ffmpegPath, effectiveUri, ct);
                if (ct.IsCancellationRequested) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;

                retryCount++;
                var safeError = SecretRedactor.Redact(ex.Message);
                _logger.LogWarning(ex, "RTSP stream error for {Camera}: {Error}", _device.DisplayName, safeError);

                if (retryCount > maxRetries)
                {
                    TransitionState(StreamState.Error, $"RTSP connection lost after {maxRetries} failed reconnection attempts: {safeError}");
                    break;
                }

                var backoffSeconds = CalculateBackoffDelaySeconds(retryCount);
                TransitionState(
                    StreamState.Reconnecting,
                    $"Connection dropped. Reconnecting in {backoffSeconds}s (attempt {retryCount}/{maxRetries})...");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task StreamFfmpegFramesAsync(string ffmpegPath, string rtspUri, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var transport = string.IsNullOrWhiteSpace(_device.RtspTransport) ? "tcp" : _device.RtspTransport;
        var args = BuildFfmpegArguments(rtspUri, transport, SocketTimeoutSeconds);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        _ffmpegProcess = process;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to launch FFmpeg media pipeline process.");
        }

        var stderrSb = new StringBuilder();
        var stderrReadTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    stderrSb.AppendLine(SecretRedactor.Redact(line));
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
                                $"FFmpeg media pipeline exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(err) ? "Stream disconnected" : err)}");
                        }
                        return;
                    }
                    headerRead += read;
                }

                if (headerBuffer[0] != 0x42 || headerBuffer[1] != 0x4D) // 'BM'
                {
                    throw new InvalidOperationException("Invalid BMP magic header received from media stream.");
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

    public static List<string> BuildFfmpegArguments(string rtspUri, string? transport = "tcp", int timeoutSeconds = 8)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "warning"
        };

        if (rtspUri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            var cleanTransport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
            args.Add("-rtsp_transport");
            args.Add(cleanTransport);
            args.Add("-stimeout");
            int effectiveTimeout = timeoutSeconds <= 0 ? 5 : timeoutSeconds;
            args.Add((effectiveTimeout * 1_000_000).ToString());
            args.Add("-fflags");
            args.Add("nobuffer");
            args.Add("-flags");
            args.Add("low_delay");
        }
        else if (rtspUri.StartsWith("testsrc=", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-f");
            args.Add("lavfi");
        }

        args.Add("-i");
        args.Add(rtspUri);
        args.Add("-f");
        args.Add("image2pipe");
        args.Add("-vcodec");
        args.Add("bmp");
        args.Add("-");

        return args;
    }

    public static string BuildAuthenticatedRtspUri(string rawUri, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return rawUri;

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
            return rawUri;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return rawUri;

        var builder = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString(username),
            Password = Uri.EscapeDataString(password)
        };

        return builder.Uri.ToString();
    }

    public static string? FindFfmpegPath()
    {
        var candidates = new[]
        {
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
            "ffmpeg",
            "ffmpeg.exe"
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var isWindows = OperatingSystem.IsWindows();
            var exeName = isWindows ? "ffmpeg.exe" : "ffmpeg";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir, exeName);
                if (File.Exists(full)) return full;
            }
        }

        return null;
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

        FrameReceived = null;
        StateChanged = null;

        await StopAsync(CancellationToken.None);
        _startStopLock.Dispose();
    }
}
