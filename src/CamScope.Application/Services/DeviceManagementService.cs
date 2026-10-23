using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CamScope.Application.Security;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.Application.Services;

public sealed class DeviceManagementService
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ICredentialStore _credentialStore;
    private readonly IOnvifClient _onvifClient;
    private readonly IAuditService _auditService;
    private readonly ILogger<DeviceManagementService> _logger;

    public DeviceManagementService(
        IDeviceRepository deviceRepository,
        ICredentialStore credentialStore,
        IOnvifClient onvifClient,
        IAuditService auditService,
        ILogger<DeviceManagementService> logger)
    {
        _deviceRepository = deviceRepository;
        _credentialStore = credentialStore;
        _onvifClient = onvifClient;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<bool> AuthenticateDeviceAsync(
        CameraDevice device,
        string username,
        string password,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty", nameof(username));

        _logger.LogInformation("Attempting authentication for device {DeviceName} ({Host})",
            device.DisplayName, device.Host);

        device.AuthState = AuthenticationState.Authenticating;
        device.ConnectionState = DeviceConnectionState.Authenticating;

        var serviceAddress = device.PrimaryServiceAddress
            ?? $"http://{device.Host}:{device.Port}/onvif/device_service";

        try
        {
            if (device.IsDemoDevice)
            {
                // In demo mode, simulate authentication
                await Task.Delay(400, ct);
                device.AuthState = AuthenticationState.Authenticated;
                device.ConnectionState = DeviceConnectionState.Connected;
                device.Capabilities = DeviceCapabilities.SupportedAll();
                await _credentialStore.SaveCredentialsAsync(device.EndpointId, username, password, ct);
                await _deviceRepository.AddOrUpdateAsync(device, ct);

                await _auditService.LogAsync(
                    AuditAction.DeviceAuthenticated,
                    device.EndpointId,
                    succeeded: true,
                    $"[DEMO] Successfully authenticated user '{username}'",
                    ct: ct);

                return true;
            }

            var info = await _onvifClient.GetDeviceInformationAsync(serviceAddress, username, password, ct);
            var caps = await _onvifClient.GetCapabilitiesAsync(serviceAddress, username, password, ct);
            var streamUri = await _onvifClient.GetStreamUriAsync(serviceAddress, username, password, ct);

            device.Vendor = string.IsNullOrWhiteSpace(info.Manufacturer) ? device.Vendor : info.Manufacturer;
            device.Model = string.IsNullOrWhiteSpace(info.Model) ? device.Model : info.Model;
            device.FirmwareVersion = info.FirmwareVersion;
            device.SerialNumber = info.SerialNumber;
            device.Capabilities = caps;
            device.RtspStreamUri = streamUri;
            device.AuthState = AuthenticationState.Authenticated;
            device.ConnectionState = DeviceConnectionState.Connected;
            device.LastError = null;
            device.LastSeenUtc = DateTime.UtcNow;

            await _credentialStore.SaveCredentialsAsync(device.EndpointId, username, password, ct);
            await _deviceRepository.AddOrUpdateAsync(device, ct);

            await _auditService.LogAsync(
                AuditAction.DeviceAuthenticated,
                device.EndpointId,
                succeeded: true,
                $"Successfully authenticated user '{username}' via ONVIF DeviceService",
                ct: ct);

            _logger.LogInformation("Device {DeviceName} authenticated successfully", device.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            device.AuthState = AuthenticationState.BadCredentials;
            device.ConnectionState = DeviceConnectionState.Reachable;
            device.LastError = ex.Message;

            var safeDetails = SecretRedactor.Redact($"Authentication failed for user '{username}': {ex.Message}");
            _logger.LogWarning("Authentication failed for device {DeviceName}: {Error}",
                device.DisplayName, safeDetails);

            await _auditService.LogAsync(
                AuditAction.AuthenticationFailed,
                device.EndpointId,
                succeeded: false,
                safeDetails,
                ct: ct);

            await _deviceRepository.AddOrUpdateAsync(device, ct);
            return false;
        }
    }

    public async Task<CameraDevice> AddManualDeviceAsync(
        string host,
        int port,
        string servicePath,
        string? username = null,
        string? password = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be empty", nameof(host));

        var cleanPath = string.IsNullOrWhiteSpace(servicePath) ? "/onvif/device_service" : servicePath;
        if (!cleanPath.StartsWith('/'))
            cleanPath = "/" + cleanPath;

        var fullServiceAddress = $"http://{host}:{port}{cleanPath}";
        var endpointId = $"manual:{host}:{port}";

        var device = new CameraDevice
        {
            EndpointId = endpointId,
            DisplayName = $"Camera @ {host}:{port}",
            Host = host,
            Port = port,
            PrimaryServiceAddress = fullServiceAddress,
            ServiceAddresses = [fullServiceAddress],
            Scopes = ["onvif://www.onvif.org/type/NetworkVideoTransmitter"],
            ConnectionState = DeviceConnectionState.Discovered,
            AuthState = AuthenticationState.Unauthenticated,
            Protocol = CameraProtocolType.Onvif,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

        await _deviceRepository.AddOrUpdateAsync(device, ct);

        await _auditService.LogAsync(
            AuditAction.DeviceAddedManually,
            endpointId,
            succeeded: true,
            $"Manually added device at {host}:{port}{cleanPath}",
            ct: ct);

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            await AuthenticateDeviceAsync(device, username, password, ct);
        }

        return device;
    }

    public async Task<CameraDevice> AddManualRtspDeviceAsync(
        string displayName,
        string rtspUrl,
        string? username = null,
        string? password = null,
        string? location = null,
        string transport = "tcp",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl))
            throw new ArgumentException("RTSP URL cannot be empty", nameof(rtspUrl));

        var cleanName = string.IsNullOrWhiteSpace(displayName) ? "RTSP Camera" : displayName.Trim();
        var host = "127.0.0.1";
        int port = 554;

        if (Uri.TryCreate(rtspUrl, UriKind.Absolute, out var parsedUri))
        {
            host = parsedUri.Host;
            port = parsedUri.Port > 0 ? parsedUri.Port : 554;
            if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(parsedUri.UserInfo))
            {
                var userInfoParts = parsedUri.UserInfo.Split(':');
                if (userInfoParts.Length > 0 && !string.IsNullOrEmpty(userInfoParts[0]))
                    username = Uri.UnescapeDataString(userInfoParts[0]);
                if (userInfoParts.Length > 1 && !string.IsNullOrEmpty(userInfoParts[1]))
                    password = Uri.UnescapeDataString(userInfoParts[1]);
            }
        }

        var endpointId = $"rtsp:{host}:{port}/{Guid.NewGuid():N}";
        bool hasAuth = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);

        var device = new CameraDevice
        {
            EndpointId = endpointId,
            DisplayName = cleanName,
            Vendor = "Generic RTSP",
            Model = "H.264/H.265 RTSP Stream",
            Host = host,
            Port = port,
            RtspStreamUri = rtspUrl.Trim(),
            RtspTransport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp",
            Location = location?.Trim(),
            Scopes = ["rtsp://video-transmitter"],
            ConnectionState = DeviceConnectionState.Connected,
            AuthState = hasAuth ? AuthenticationState.Authenticated : AuthenticationState.Unauthenticated,
            Capabilities = new DeviceCapabilities
            {
                LiveStreaming = CapabilityStatus.Supported,
                Ptz = CapabilityStatus.Unsupported,
                RecordingSearch = CapabilityStatus.Unsupported,
                Analytics = CapabilityStatus.Unsupported
            },
            Protocol = CameraProtocolType.RTSP,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

        if (hasAuth)
        {
            await _credentialStore.SaveCredentialsAsync(endpointId, username!, password!, ct);
        }

        await _deviceRepository.AddOrUpdateAsync(device, ct);

        await _auditService.LogAsync(
            AuditAction.DeviceAddedManually,
            endpointId,
            succeeded: true,
            $"Manually added direct RTSP device '{cleanName}' ({SecretRedactor.Redact(rtspUrl)})",
            ct: ct);

        return device;
    }

    public async Task<CameraConnectionTestResult> TestDeviceConnectionAsync(CameraDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var sw = Stopwatch.StartNew();
        CameraConnectionTestResult result;

        if (device.Protocol == CameraProtocolType.Onvif && !string.IsNullOrWhiteSpace(device.PrimaryServiceAddress))
        {
            result = await _onvifClient.TestConnectionAsync(device.PrimaryServiceAddress, TimeSpan.FromSeconds(3), ct);
        }
        else
        {
            // Direct RTSP or TCP probe
            result = new CameraConnectionTestResult();
            var port = device.Port > 0 ? device.Port : 554;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                await socket.ConnectAsync(device.Host, port, linked.Token);

                sw.Stop();
                result.IsReachable = true;
                result.IsRtspPortOpen = true;
                result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                result.StatusMessage = $"Reachable ({result.LatencyMs}ms) - Port {port} Open";
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.IsReachable = false;
                result.LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                result.StatusMessage = $"Unreachable: {ex.Message}";
                result.DiagnosticDetails = ex.ToString();
            }
        }

        device.LastMeasuredLatencyMs = result.LatencyMs;
        device.LastSeenUtc = DateTime.UtcNow;
        if (result.IsReachable && device.ConnectionState == DeviceConnectionState.Unavailable)
        {
            device.ConnectionState = DeviceConnectionState.Reachable;
        }

        return result;
    }

    public async Task DeleteDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = await _deviceRepository.GetByIdAsync(deviceId, ct);
        if (device != null)
        {
            await _credentialStore.DeleteCredentialsAsync(device.EndpointId, ct);
            await _deviceRepository.DeleteAsync(deviceId, ct);

            await _auditService.LogAsync(
                AuditAction.DeviceRemoved,
                device.EndpointId,
                succeeded: true,
                $"Removed device {device.DisplayName} ({device.Host}) from inventory",
                ct: ct);
        }
    }

    public async Task<string> ExportInventoryReportAsync(string format, CancellationToken ct = default)
    {
        var devices = await _deviceRepository.GetAllAsync(ct);

        await _auditService.LogAsync(
            AuditAction.InventoryExported,
            null,
            succeeded: true,
            $"Exported {devices.Count} devices in {format.ToUpperInvariant()} format",
            ct: ct);

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,DisplayName,Vendor,Model,Host,Port,ConnectionState,AuthState,LiveStreaming,RecordingSearch,Location,Tags,LastSeenUtc");
            foreach (var d in devices)
            {
                sb.AppendLine(
                    $"\"{d.Id}\",\"{EscapeCsv(d.DisplayName)}\",\"{EscapeCsv(d.Vendor)}\",\"{EscapeCsv(d.Model)}\"," +
                    $"\"{d.Host}\",{d.Port},\"{d.ConnectionState}\",\"{d.AuthState}\"," +
                    $"\"{d.Capabilities.LiveStreaming}\",\"{d.Capabilities.RecordingSearch}\"," +
                    $"\"{EscapeCsv(d.Location ?? "")}\",\"{EscapeCsv(string.Join(";", d.Tags))}\",\"{d.LastSeenUtc:o}\"");
            }
            return sb.ToString();
        }

        // JSON format (excluding any secrets)
        var exportDto = devices.Select(d => new
        {
            d.Id,
            d.EndpointId,
            d.DisplayName,
            d.Vendor,
            d.Model,
            d.FirmwareVersion,
            d.Host,
            d.Port,
            d.PrimaryServiceAddress,
            d.ConnectionState,
            d.AuthState,
            d.Capabilities,
            d.Location,
            d.Tags,
            d.LastSeenUtc,
            d.IsDemoDevice
        });

        return JsonSerializer.Serialize(exportDto, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"");
}
