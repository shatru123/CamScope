using System.Text.Json;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Data.Sqlite;

namespace CamScope.Persistence.Repositories;

public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly string _connectionString;

    public SqliteDeviceRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<CameraDevice>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Devices ORDER BY LastSeenUtc DESC";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CameraDevice>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadDevice(reader));
        }

        return list;
    }

    public async Task<CameraDevice?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Devices WHERE Id = @Id LIMIT 1";
        cmd.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadDevice(reader);
        }

        return null;
    }

    public async Task<CameraDevice?> GetByEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Devices WHERE EndpointId = @EndpointId LIMIT 1";
        cmd.Parameters.AddWithValue("@EndpointId", endpointId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadDevice(reader);
        }

        return null;
    }

    public async Task AddOrUpdateAsync(CameraDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Devices (
    Id, EndpointId, DisplayName, Vendor, Model, FirmwareVersion, SerialNumber,
    Host, Port, PrimaryServiceAddress, ServiceAddressesJson, ScopesJson,
    ConnectionState, AuthState, CapabilitiesJson, Protocol, FirstSeenUtc,
    LastSeenUtc, LastError, IsDemoDevice, Location, TagsJson, RtspStreamUri
) VALUES (
    @Id, @EndpointId, @DisplayName, @Vendor, @Model, @FirmwareVersion, @SerialNumber,
    @Host, @Port, @PrimaryServiceAddress, @ServiceAddressesJson, @ScopesJson,
    @ConnectionState, @AuthState, @CapabilitiesJson, @Protocol, @FirstSeenUtc,
    @LastSeenUtc, @LastError, @IsDemoDevice, @Location, @TagsJson, @RtspStreamUri
)
ON CONFLICT(EndpointId) DO UPDATE SET
    DisplayName = excluded.DisplayName,
    Vendor = excluded.Vendor,
    Model = excluded.Model,
    FirmwareVersion = excluded.FirmwareVersion,
    SerialNumber = excluded.SerialNumber,
    Host = excluded.Host,
    Port = excluded.Port,
    PrimaryServiceAddress = excluded.PrimaryServiceAddress,
    ServiceAddressesJson = excluded.ServiceAddressesJson,
    ScopesJson = excluded.ScopesJson,
    ConnectionState = excluded.ConnectionState,
    AuthState = excluded.AuthState,
    CapabilitiesJson = excluded.CapabilitiesJson,
    Protocol = excluded.Protocol,
    LastSeenUtc = excluded.LastSeenUtc,
    LastError = excluded.LastError,
    Location = excluded.Location,
    TagsJson = excluded.TagsJson,
    RtspStreamUri = excluded.RtspStreamUri;
";
        cmd.Parameters.AddWithValue("@Id", device.Id.ToString());
        cmd.Parameters.AddWithValue("@EndpointId", device.EndpointId);
        cmd.Parameters.AddWithValue("@DisplayName", device.DisplayName);
        cmd.Parameters.AddWithValue("@Vendor", device.Vendor);
        cmd.Parameters.AddWithValue("@Model", device.Model);
        cmd.Parameters.AddWithValue("@FirmwareVersion", (object?)device.FirmwareVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SerialNumber", (object?)device.SerialNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Host", device.Host);
        cmd.Parameters.AddWithValue("@Port", device.Port);
        cmd.Parameters.AddWithValue("@PrimaryServiceAddress", (object?)device.PrimaryServiceAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ServiceAddressesJson", JsonSerializer.Serialize(device.ServiceAddresses));
        cmd.Parameters.AddWithValue("@ScopesJson", JsonSerializer.Serialize(device.Scopes));
        cmd.Parameters.AddWithValue("@ConnectionState", device.ConnectionState.ToString());
        cmd.Parameters.AddWithValue("@AuthState", device.AuthState.ToString());
        cmd.Parameters.AddWithValue("@CapabilitiesJson", JsonSerializer.Serialize(device.Capabilities));
        cmd.Parameters.AddWithValue("@Protocol", device.Protocol.ToString());
        cmd.Parameters.AddWithValue("@FirstSeenUtc", device.FirstSeenUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@LastSeenUtc", device.LastSeenUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@LastError", (object?)device.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsDemoDevice", device.IsDemoDevice ? 1 : 0);
        cmd.Parameters.AddWithValue("@Location", (object?)device.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TagsJson", JsonSerializer.Serialize(device.Tags));
        cmd.Parameters.AddWithValue("@RtspStreamUri", (object?)device.RtspStreamUri ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Devices WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id.ToString());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Devices";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static CameraDevice ReadDevice(SqliteDataReader reader)
    {
        return new CameraDevice
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            EndpointId = reader.GetString(reader.GetOrdinal("EndpointId")),
            DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
            Vendor = reader.GetString(reader.GetOrdinal("Vendor")),
            Model = reader.GetString(reader.GetOrdinal("Model")),
            FirmwareVersion = reader.IsDBNull(reader.GetOrdinal("FirmwareVersion")) ? "" : reader.GetString(reader.GetOrdinal("FirmwareVersion")),
            SerialNumber = reader.IsDBNull(reader.GetOrdinal("SerialNumber")) ? "" : reader.GetString(reader.GetOrdinal("SerialNumber")),
            Host = reader.GetString(reader.GetOrdinal("Host")),
            Port = reader.GetInt32(reader.GetOrdinal("Port")),
            PrimaryServiceAddress = reader.IsDBNull(reader.GetOrdinal("PrimaryServiceAddress")) ? null : reader.GetString(reader.GetOrdinal("PrimaryServiceAddress")),
            ServiceAddresses = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("ServiceAddressesJson"))) ?? [],
            Scopes = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("ScopesJson"))) ?? [],
            ConnectionState = Enum.Parse<DeviceConnectionState>(reader.GetString(reader.GetOrdinal("ConnectionState"))),
            AuthState = Enum.Parse<AuthenticationState>(reader.GetString(reader.GetOrdinal("AuthState"))),
            Capabilities = JsonSerializer.Deserialize<DeviceCapabilities>(reader.GetString(reader.GetOrdinal("CapabilitiesJson"))) ?? new DeviceCapabilities(),
            Protocol = Enum.Parse<CameraProtocolType>(reader.GetString(reader.GetOrdinal("Protocol"))),
            FirstSeenUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("FirstSeenUtc"))).ToUniversalTime(),
            LastSeenUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastSeenUtc"))).ToUniversalTime(),
            LastError = reader.IsDBNull(reader.GetOrdinal("LastError")) ? null : reader.GetString(reader.GetOrdinal("LastError")),
            IsDemoDevice = reader.GetInt32(reader.GetOrdinal("IsDemoDevice")) == 1,
            Location = reader.IsDBNull(reader.GetOrdinal("Location")) ? null : reader.GetString(reader.GetOrdinal("Location")),
            Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("TagsJson"))) ?? [],
            RtspStreamUri = reader.IsDBNull(reader.GetOrdinal("RtspStreamUri")) ? null : reader.GetString(reader.GetOrdinal("RtspStreamUri"))
        };
    }
}
