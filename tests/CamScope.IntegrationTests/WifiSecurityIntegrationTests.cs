using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Persistence;
using CamScope.Persistence.Repositories;
using Xunit;

namespace CamScope.IntegrationTests;

public sealed class WifiSecurityIntegrationTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;
    private readonly DatabaseInitializer _initializer;
    private readonly SqliteWifiSecurityRepository _repository;

    public WifiSecurityIntegrationTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"camscope_wifi_test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
        _initializer = new DatabaseInitializer(_connectionString);
        _initializer.Initialize();
        _repository = new SqliteWifiSecurityRepository(_connectionString);
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    [Fact]
    public async Task ScanSessions_SaveAndRetrieve_RoundTripsProperly()
    {
        var session = new WifiScanSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ScannedAtUtc = DateTime.UtcNow,
            TotalNetworksFound = 5,
            OverallPosture = SecurityPostureLevel.NeedsAttention,
            ObservationsJson = "[{\"Ssid\":\"TestNet\",\"Channel\":6}]",
            FindingsSummaryJson = "[{\"Title\":\"WPS Enabled\",\"Severity\":\"Medium\"}]"
        };

        await _repository.SaveScanSessionAsync(session);

        var history = await _repository.GetScanHistoryAsync(10);
        Assert.NotEmpty(history);
        var retrieved = history.First(h => h.Id == session.Id);
        Assert.Equal(5, retrieved.TotalNetworksFound);
        Assert.Equal(SecurityPostureLevel.NeedsAttention, retrieved.OverallPosture);
        Assert.Equal(session.ObservationsJson, retrieved.ObservationsJson);
        Assert.Equal(session.FindingsSummaryJson, retrieved.FindingsSummaryJson);
    }

    [Fact]
    public async Task ScanSessions_ClearHistory_RemovesAllRecords()
    {
        var session = new WifiScanSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ScannedAtUtc = DateTime.UtcNow,
            TotalNetworksFound = 3,
            OverallPosture = SecurityPostureLevel.Satisfactory
        };

        await _repository.SaveScanSessionAsync(session);
        var beforeClear = await _repository.GetScanHistoryAsync();
        Assert.NotEmpty(beforeClear);

        await _repository.ClearScanHistoryAsync();
        var afterClear = await _repository.GetScanHistoryAsync();
        Assert.Empty(afterClear);
    }

    [Fact]
    public async Task AuditRecords_SaveAndRetrieveBySsid_PreservesChecklistItems()
    {
        var record = new AuthorizedAuditRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Ssid = "TargetMesh_Sec",
            Bssid = "11:22:33:44:55:66",
            AuditedAtUtc = DateTime.UtcNow,
            OwnershipConfirmed = true,
            RouterVerified = true,
            OperatorNotes = "Verified via Netgear Orbi web administration",
            Items =
            [
                new AuditChecklistItem
                {
                    Title = "WPS Disabled",
                    Category = "Hardening",
                    IsVerified = true,
                    Description = "PIN disabled"
                },
                new AuditChecklistItem
                {
                    Title = "PMF Enforced",
                    Category = "Hardening",
                    IsVerified = true,
                    Description = "802.11w required"
                }
            ]
        };

        await _repository.SaveAuditRecordAsync(record);

        var records = await _repository.GetAuditRecordsAsync("TargetMesh_Sec");
        Assert.NotEmpty(records);
        var retrieved = records.First(r => r.Id == record.Id);
        Assert.Equal("TargetMesh_Sec", retrieved.Ssid);
        Assert.True(retrieved.OwnershipConfirmed);
        Assert.True(retrieved.RouterVerified);
        Assert.Equal("Verified via Netgear Orbi web administration", retrieved.OperatorNotes);
        Assert.Equal(2, retrieved.Items.Count);
        Assert.All(retrieved.Items, item => Assert.True(item.IsVerified));
    }
}
