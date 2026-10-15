using System.Text.Json;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Data.Sqlite;

namespace CamScope.Persistence.Services;

public sealed class SqliteAuditService : IAuditService
{
    private readonly string _connectionString;

    public SqliteAuditService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task LogAsync(
        AuditAction action,
        string? targetEndpoint,
        bool succeeded,
        string details,
        string? userOrSource = null,
        CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AuditLogs (Id, TimestampUtc, Action, TargetEndpoint, UserOrSource, Succeeded, Details)
VALUES (@Id, @TimestampUtc, @Action, @TargetEndpoint, @UserOrSource, @Succeeded, @Details);
";
        cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@TimestampUtc", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@Action", action.ToString());
        cmd.Parameters.AddWithValue("@TargetEndpoint", (object?)targetEndpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UserOrSource", (object?)userOrSource ?? "LocalUser");
        cmd.Parameters.AddWithValue("@Succeeded", succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("@Details", details);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int count = 100, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM AuditLogs ORDER BY TimestampUtc DESC LIMIT @Count";
        cmd.Parameters.AddWithValue("@Count", count);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ActivityAuditEntry>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ActivityAuditEntry
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                TimestampUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("TimestampUtc"))).ToUniversalTime(),
                Action = Enum.Parse<AuditAction>(reader.GetString(reader.GetOrdinal("Action"))),
                TargetEndpoint = reader.IsDBNull(reader.GetOrdinal("TargetEndpoint")) ? null : reader.GetString(reader.GetOrdinal("TargetEndpoint")),
                UserOrSource = reader.IsDBNull(reader.GetOrdinal("UserOrSource")) ? "LocalUser" : reader.GetString(reader.GetOrdinal("UserOrSource")),
                Succeeded = reader.GetInt32(reader.GetOrdinal("Succeeded")) == 1,
                Details = reader.GetString(reader.GetOrdinal("Details"))
            });
        }

        return list;
    }

    public async Task ClearLogsAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AuditLogs";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class SqliteSettingsService : ISettingsService
{
    private const string SettingsKey = "AppSettings";
    private readonly string _connectionString;

    public SqliteSettingsService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<ApplicationSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ValueJson FROM Settings WHERE Key = @Key LIMIT 1";
        cmd.Parameters.AddWithValue("@Key", SettingsKey);

        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ApplicationSettings();
        }

        return JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();
    }

    public async Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Settings (Key, ValueJson) VALUES (@Key, @Value)
ON CONFLICT(Key) DO UPDATE SET ValueJson = excluded.ValueJson;
";
        cmd.Parameters.AddWithValue("@Key", SettingsKey);
        cmd.Parameters.AddWithValue("@Value", JsonSerializer.Serialize(settings));

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
