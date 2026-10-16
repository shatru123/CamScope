using CamScope.App.ViewModels;
using CamScope.Application.Security;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.ViewModels;

public class WifiSecurityViewModelTests
{
    private sealed class FakeWifiSecurityScanner : IWifiSecurityScanner
    {
        public bool IsPlatformSupported { get; set; } = true;
        public List<WifiAccessPointObservation> Observations { get; set; } = [];
        public bool Succeeded { get; set; } = true;
        public string? ErrorMessage { get; set; }

        public Task<WifiScanSessionResult> ScanAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new WifiScanSessionResult
            {
                Succeeded = Succeeded,
                ErrorMessage = ErrorMessage,
                Observations = Observations,
                ScannedAtUtc = DateTime.UtcNow
            });
        }
    }

    private sealed class FakeWifiSecurityRepository : IWifiSecurityRepository
    {
        public List<WifiScanSession> Sessions { get; } = [];
        public List<AuthorizedAuditRecord> Records { get; } = [];

        public Task SaveScanSessionAsync(WifiScanSession session, CancellationToken ct = default)
        {
            Sessions.Add(session);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WifiScanSession>> GetScanHistoryAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WifiScanSession>>(Sessions.Take(limit).ToList());

        public Task DeleteScanSessionAsync(string id, CancellationToken ct = default)
        {
            Sessions.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearScanHistoryAsync(CancellationToken ct = default)
        {
            Sessions.Clear();
            return Task.CompletedTask;
        }

        public Task SaveAuditRecordAsync(AuthorizedAuditRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuthorizedAuditRecord>> GetAuditRecordsAsync(string? ssid = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ssid))
                return Task.FromResult<IReadOnlyList<AuthorizedAuditRecord>>(Records);
            return Task.FromResult<IReadOnlyList<AuthorizedAuditRecord>>(Records.Where(r => r.Ssid == ssid).ToList());
        }

        public Task DeleteAuditRecordAsync(string id, CancellationToken ct = default)
        {
            Records.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public List<ActivityAuditEntry> Entries { get; } = [];

        public Task LogAsync(AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = "LocalUser", CancellationToken ct = default)
        {
            Entries.Add(new ActivityAuditEntry
            {
                Action = action,
                TargetEndpoint = targetEndpoint,
                Succeeded = succeeded,
                Details = details,
                UserOrSource = userOrSource,
                TimestampUtc = DateTime.UtcNow
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int maxCount = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>(Entries);

        public Task ClearLogsAsync(CancellationToken ct = default)
        {
            Entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public ApplicationSettings Settings { get; set; } = new();

        public Task<ApplicationSettings> LoadSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(Settings);

        public Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken ct = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private (WifiSecurityViewModel vm, FakeWifiSecurityScanner scanner, FakeWifiSecurityRepository repo, FakeAuditService audit) CreateViewModel(List<WifiAccessPointObservation>? sampleAps = null)
    {
        var scanner = new FakeWifiSecurityScanner();
        if (sampleAps != null) scanner.Observations = sampleAps;

        var analyzer = new WifiSecurityAnalyzer();
        var estimator = new PasswordStrengthEstimator();
        var repo = new FakeWifiSecurityRepository();
        var audit = new FakeAuditService();
        var settings = new FakeSettingsService();
        var logger = NullLogger<WifiSecurityViewModel>.Instance;

        var vm = new WifiSecurityViewModel(scanner, analyzer, estimator, repo, audit, settings, logger);
        return (vm, scanner, repo, audit);
    }

    [Fact]
    public async Task StartScanAsync_DiscoversNetworksAndComputesOverallPosture()
    {
        var sampleAps = new List<WifiAccessPointObservation>
        {
            new() { Ssid = "Office_5G", Bssid = "00:11:22:33:44:01", SecurityStandard = WifiSecurityStandard.Wpa3Personal, Channel = 36, Band = "5 GHz", Rssi = -50 },
            new() { Ssid = "Guest_Open", Bssid = "00:11:22:33:44:02", SecurityStandard = WifiSecurityStandard.Open, Channel = 6, Band = "2.4 GHz", Rssi = -70 }
        };

        var (vm, _, repo, audit) = CreateViewModel(sampleAps);
        await vm.StartScanAsync();

        Assert.Equal(2, vm.TotalAccessPoints);
        Assert.Equal(1, vm.TotalOpenNetworks);
        Assert.Equal(1, vm.TotalWpa3Networks);
        Assert.Equal(1, vm.HighFindingsCount);
        Assert.Equal("VULNERABLE", vm.OverallPostureBadgeText);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.WifiSecurityScanStarted);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.WifiSecurityScanCompleted);
        Assert.NotEmpty(repo.Sessions);
    }

    [Fact]
    public async Task FilterBySearchQuery_MatchesSsidAndBssid()
    {
        var sampleAps = new List<WifiAccessPointObservation>
        {
            new() { Ssid = "TargetNetwork", Bssid = "AA:BB:CC:DD:EE:01", SecurityStandard = WifiSecurityStandard.Wpa2Personal, Channel = 1 },
            new() { Ssid = "OtherSsid", Bssid = "11:22:33:44:55:66", SecurityStandard = WifiSecurityStandard.Wpa2Personal, Channel = 6 }
        };

        var (vm, _, _, _) = CreateViewModel(sampleAps);
        await vm.StartScanAsync();

        Assert.Equal(2, vm.FilteredAccessPoints.Count);

        // Search by SSID
        vm.SearchQuery = "target";
        Assert.Single(vm.FilteredAccessPoints);
        Assert.Equal("TargetNetwork", vm.FilteredAccessPoints[0].Ssid);

        // Search by BSSID
        vm.SearchQuery = "11:22";
        Assert.Single(vm.FilteredAccessPoints);
        Assert.Equal("OtherSsid", vm.FilteredAccessPoints[0].Ssid);
    }

    [Fact]
    public async Task FilterByBand_RestrictsToSelectedFrequencyBand()
    {
        var sampleAps = new List<WifiAccessPointObservation>
        {
            new() { Ssid = "Band24", Channel = 1, Band = "2.4 GHz", SecurityStandard = WifiSecurityStandard.Wpa2Personal },
            new() { Ssid = "Band5", Channel = 36, Band = "5 GHz", SecurityStandard = WifiSecurityStandard.Wpa2Personal },
            new() { Ssid = "Band6", Channel = 1, Band = "6 GHz", SecurityStandard = WifiSecurityStandard.Wpa3Personal }
        };

        var (vm, _, _, _) = CreateViewModel(sampleAps);
        await vm.StartScanAsync();

        vm.SelectedBandFilter = "5 GHz";
        Assert.Single(vm.FilteredAccessPoints);
        Assert.Equal("Band5", vm.FilteredAccessPoints[0].Ssid);

        vm.SelectedBandFilter = "2.4 GHz";
        Assert.Single(vm.FilteredAccessPoints);
        Assert.Equal("Band24", vm.FilteredAccessPoints[0].Ssid);

        vm.SelectedBandFilter = "6 GHz";
        Assert.Single(vm.FilteredAccessPoints);
        Assert.Equal("Band6", vm.FilteredAccessPoints[0].Ssid);
    }

    [Fact]
    public async Task DetailDrawer_Selection_LoadsApSpecificFindings()
    {
        var apOpen = new WifiAccessPointObservation
        {
            Id = "ap-1",
            Ssid = "OpenWifi",
            SecurityStandard = WifiSecurityStandard.Open,
            Channel = 6,
            Band = "2.4 GHz"
        };
        var apWpa3 = new WifiAccessPointObservation
        {
            Id = "ap-2",
            Ssid = "SecureWifi",
            SecurityStandard = WifiSecurityStandard.Wpa3Personal,
            Channel = 36,
            Band = "5 GHz"
        };

        var (vm, _, _, _) = CreateViewModel([apOpen, apWpa3]);
        await vm.StartScanAsync();

        vm.OpenDetailDrawer(apOpen);
        Assert.True(vm.IsDetailDrawerOpen);
        Assert.Equal("OpenWifi", vm.SelectedAccessPoint?.Ssid);
        Assert.Contains(vm.SelectedApFindings, f => f.Severity == FindingSeverity.High);

        vm.CloseDetailDrawer();
        Assert.False(vm.IsDetailDrawerOpen);
        Assert.Null(vm.SelectedAccessPoint);
        Assert.Empty(vm.SelectedApFindings);
    }

    [Fact]
    public async Task AuthorizedAuditModal_EnforcesOwnershipConfirmationBeforeSaving()
    {
        var ap = new WifiAccessPointObservation
        {
            Ssid = "MyRouter_5G",
            Bssid = "00:11:22:33:44:55",
            SecurityStandard = WifiSecurityStandard.Wpa2Personal
        };

        var (vm, _, repo, audit) = CreateViewModel([ap]);
        await vm.StartScanAsync();

        vm.OpenAuditModal(ap);
        Assert.True(vm.IsAuditModalOpen);
        Assert.Equal("MyRouter_5G", vm.AuditTargetSsid);
        Assert.False(vm.IsOwnershipConfirmed);

        // Attempt save without confirmation
        await vm.SaveAuditRecordAsync();
        Assert.True(vm.HasError);
        Assert.True(vm.IsAuditModalOpen); // Remains open
        Assert.Empty(repo.Records);

        // Confirm ownership and verify
        vm.IsOwnershipConfirmed = true;
        vm.IsRouterVerified = true;
        vm.AuditNotes = "Inspected ASUS RT-AX88U firmware 388.5; WPS is toggled off; PMF enabled.";

        await vm.SaveAuditRecordAsync();

        Assert.False(vm.IsAuditModalOpen);
        Assert.Single(repo.Records);
        var record = repo.Records[0];
        Assert.Equal("MyRouter_5G", record.Ssid);
        Assert.True(record.OwnershipConfirmed);
        Assert.True(record.RouterVerified);
        Assert.Contains(audit.Entries, e => e.Action == AuditAction.WifiAuditRecordSaved);
    }

    [Fact]
    public void PasswordStrengthTool_EvaluatesInRealTimeAndGuaranteesZeroRetentionOnClose()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.OpenPasswordModal();
        Assert.True(vm.IsPasswordModalOpen);
        Assert.Equal(string.Empty, vm.CandidatePasswordInput);

        // Simulate operator typing a candidate password
        vm.CandidatePasswordInput = "Tr0ub4dor&3!";
        Assert.NotNull(vm.PasswordEstimate);
        Assert.True(vm.PasswordEstimate.EntropyBits > 30.0);

        // Close modal -> verifies zero-retention guarantee
        vm.ClosePasswordModal();
        Assert.False(vm.IsPasswordModalOpen);
        Assert.Equal(string.Empty, vm.CandidatePasswordInput);
        Assert.Null(vm.PasswordEstimate);
    }
}
