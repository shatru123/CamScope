using System.Text.Json;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Data.Sqlite;

namespace CamScope.Persistence.Repositories;

public sealed class SqliteWifiSecurityRepository : IWifiSecurityRepository
{
    private readonly string _connectionString;

    public SqliteWifiSecurityRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveScanSessionAsync(WifiScanSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO WifiScanSessions (Id, ScannedAtUtc, TotalNetworksFound, OverallPosture, ObservationsJson, FindingsSummaryJson)
VALUES ($id, $scannedAt, $total, $posture, $obs, $findings);";

        cmd.Parameters.AddWithValue("$id", session.Id);
        cmd.Parameters.AddWithValue("$scannedAt", session.ScannedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$total", session.TotalNetworksFound);
        cmd.Parameters.AddWithValue("$posture", session.OverallPosture.ToString());
        cmd.Parameters.AddWithValue("$obs", session.ObservationsJson ?? "[]");
        cmd.Parameters.AddWithValue("$findings", session.FindingsSummaryJson ?? "[]");

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WifiScanSession>> GetScanHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        var list = new List<WifiScanSession>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, ScannedAtUtc, TotalNetworksFound, OverallPosture, ObservationsJson, FindingsSummaryJson
FROM WifiScanSessions
ORDER BY ScannedAtUtc DESC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var postureStr = reader.GetString(3);
            _ = Enum.TryParse<SecurityPostureLevel>(postureStr, out var posture);

            list.Add(new WifiScanSession
            {
                Id = reader.GetString(0),
                ScannedAtUtc = DateTime.Parse(reader.GetString(1)),
                TotalNetworksFound = reader.GetInt32(2),
                OverallPosture = posture,
                ObservationsJson = reader.GetString(4),
                FindingsSummaryJson = reader.GetString(5)
            });
        }

        return list;
    }

    public async Task DeleteScanSessionAsync(string id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WifiScanSessions WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearScanHistoryAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WifiScanSessions;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAuditRecordAsync(AuthorizedAuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR REPLACE INTO WifiAuditRecords (Id, Ssid, Bssid, AuditedAtUtc, OwnershipConfirmed, ChecklistJson, Notes, RouterVerified)
VALUES ($id, $ssid, $bssid, $auditedAt, $owned, $checklist, $notes, $verified);";

        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$ssid", record.Ssid);
        cmd.Parameters.AddWithValue("$bssid", (object?)record.Bssid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$auditedAt", record.AuditedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$owned", record.OwnershipConfirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$checklist", JsonSerializer.Serialize(record.Items));
        cmd.Parameters.AddWithValue("$notes", (object?)record.OperatorNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$verified", record.RouterVerified ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuthorizedAuditRecord>> GetAuditRecordsAsync(string? ssid = null, CancellationToken ct = default)
    {
        var list = new List<AuthorizedAuditRecord>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(ssid))
        {
            cmd.CommandText = @"
SELECT Id, Ssid, Bssid, AuditedAtUtc, OwnershipConfirmed, ChecklistJson, Notes, RouterVerified
FROM WifiAuditRecords
ORDER BY AuditedAtUtc DESC;";
        }
        else
        {
            cmd.CommandText = @"
SELECT Id, Ssid, Bssid, AuditedAtUtc, OwnershipConfirmed, ChecklistJson, Notes, RouterVerified
FROM WifiAuditRecords
WHERE Ssid = $ssid
ORDER BY AuditedAtUtc DESC;";
            cmd.Parameters.AddWithValue("$ssid", ssid);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var checklistJson = reader.GetString(5);
            var items = JsonSerializer.Deserialize<List<AuditChecklistItem>>(checklistJson) ?? [];

            list.Add(new AuthorizedAuditRecord
            {
                Id = reader.GetString(0),
                Ssid = reader.GetString(1),
                Bssid = reader.IsDBNull(2) ? null : reader.GetString(2),
                AuditedAtUtc = DateTime.Parse(reader.GetString(3)),
                OwnershipConfirmed = reader.GetInt32(4) == 1,
                Items = items,
                OperatorNotes = reader.IsDBNull(6) ? null : reader.GetString(6),
                RouterVerified = reader.GetInt32(7) == 1
            });
        }

        return list;
    }

    public async Task DeleteAuditRecordAsync(string id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WifiAuditRecords WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
