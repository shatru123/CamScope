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
