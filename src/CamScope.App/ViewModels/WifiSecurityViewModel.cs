using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CamScope.App.ViewModels;

public sealed partial class WifiSecurityViewModel : ViewModelBase, IDisposable
{
    private readonly IWifiSecurityScanner _scanner;
    private readonly IWifiSecurityAnalyzer _analyzer;
    private readonly IPasswordStrengthEstimator _passwordEstimator;
    private readonly IWifiSecurityRepository _repository;
    private readonly IAuditService _auditService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<WifiSecurityViewModel> _logger;

    private CancellationTokenSource? _scanCts;
    private bool _disposed;
    private readonly List<WifiAccessPointObservation> _allAccessPoints = [];
    private SecurityAssessmentReport? _currentReport;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatusMessage = "Ready to perform passive Wi-Fi security assessment.";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _lastScanTimeText = "Never";
    [ObservableProperty] private string? _exportFeedback;
    [ObservableProperty] private bool _isDemoMode;

    // Filters and Search
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedSeverityFilter = "All";
    [ObservableProperty] private string _selectedBandFilter = "All";
    [ObservableProperty] private string _selectedSortOption = "Highest Severity";

    public IReadOnlyList<string> SeverityFilterOptions { get; } = ["All", "Critical", "High", "Medium", "Low", "Informational"];
    public IReadOnlyList<string> BandFilterOptions { get; } = ["All", "6 GHz", "5 GHz", "2.4 GHz"];
    public IReadOnlyList<string> SortOptions { get; } = ["Highest Severity", "Signal Strength", "SSID (A-Z)", "Channel"];

    // Dashboard Metrics
    [ObservableProperty] private int _totalAccessPoints;
    [ObservableProperty] private int _totalOpenNetworks;
    [ObservableProperty] private int _totalWpa3Networks;
    [ObservableProperty] private int _criticalFindingsCount;
    [ObservableProperty] private int _highFindingsCount;
    [ObservableProperty] private int _mediumFindingsCount;
    [ObservableProperty] private string _overallPostureBadgeText = "SECURE";
    [ObservableProperty] private string _overallPostureBadgeColor = "#10B981";
    [ObservableProperty] private string _postureSummaryText = "No security assessment run yet.";

    // Active Selection & Detail Drawer
    [ObservableProperty] private WifiAccessPointObservation? _selectedAccessPoint;
    [ObservableProperty] private bool _isDetailDrawerOpen;

    // Authorized Audit Modal
    [ObservableProperty] private bool _isAuditModalOpen;
    [ObservableProperty] private string _auditTargetSsid = string.Empty;
    [ObservableProperty] private string? _auditTargetBssid;
    [ObservableProperty] private bool _isOwnershipConfirmed;
    [ObservableProperty] private string _auditNotes = string.Empty;
    [ObservableProperty] private bool _isRouterVerified;

    // Password Strength Estimator Tool Modal (Zero retention)
    [ObservableProperty] private bool _isPasswordModalOpen;
    [ObservableProperty] private string _candidatePasswordInput = string.Empty;
    [ObservableProperty] private PasswordStrengthEstimate? _passwordEstimate;

    // Collections
    public ObservableCollection<WifiAccessPointObservation> FilteredAccessPoints { get; } = [];
    public ObservableCollection<SecurityFinding> SelectedApFindings { get; } = [];
    public ObservableCollection<DuplicateSsidAnalysis> DuplicateSsidAnalyses { get; } = [];
    public ObservableCollection<ChannelCongestionMetric> CongestionMetrics { get; } = [];
    public ObservableCollection<WifiScanSession> ScanHistory { get; } = [];
    public ObservableCollection<AuditChecklistItem> AuditChecklistItems { get; } = [];

    public WifiSecurityViewModel(
        IWifiSecurityScanner scanner,
        IWifiSecurityAnalyzer analyzer,
        IPasswordStrengthEstimator passwordEstimator,
        IWifiSecurityRepository repository,
        IAuditService auditService,
        ISettingsService settingsService,
        ILogger<WifiSecurityViewModel> logger)
    {
        _scanner = scanner;
        _analyzer = analyzer;
        _passwordEstimator = passwordEstimator;
        _repository = repository;
        _auditService = auditService;
        _settingsService = settingsService;
        _logger = logger;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var settings = await _settingsService.LoadSettingsAsync();
            IsDemoMode = settings.IsDemoModeEnabled;
            await LoadScanHistoryAsync();
            await StartScanAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Wi-Fi Security ViewModel");
        }
    }

    [RelayCommand]
    public async Task StartScanAsync()
    {
        if (IsScanning) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        HasError = false;
        ErrorMessage = null;
        ScanStatusMessage = "Passively capturing nearby access point beacon telemetry...";

        await _auditService.LogAsync(
            AuditAction.WifiSecurityScanStarted,
            "LocalWifiAdapter",
            succeeded: true,
            "Operator initiated passive Wi-Fi security assessment scan").ConfigureAwait(false);

        try
        {
            var scanResult = await _scanner.ScanAsync(_scanCts.Token);

            if (!scanResult.Succeeded)
            {
                RunOnUI(() =>
                {
                    HasError = true;
                    ErrorMessage = scanResult.ErrorMessage ?? "Wi-Fi scan failed.";
                    ScanStatusMessage = "Scan terminated with errors.";
                });
                return;
            }

            // Execute passive findings analysis
            var report = _analyzer.Analyze(scanResult.Observations);

            RunOnUI(() =>
            {
                _allAccessPoints.Clear();
                _allAccessPoints.AddRange(scanResult.Observations);
                _currentReport = report;

                // Update dashboard metrics
                TotalAccessPoints = report.TotalAccessPoints;
                TotalOpenNetworks = report.TotalOpenNetworks;
                TotalWpa3Networks = report.TotalWpa3Networks;
                CriticalFindingsCount = report.CriticalFindingsCount;
                HighFindingsCount = report.HighFindingsCount;
                MediumFindingsCount = report.MediumFindingsCount;
                OverallPostureBadgeText = report.PostureBadgeText;
                OverallPostureBadgeColor = report.PostureBadgeColor;
                PostureSummaryText = report.PostureSummary;
                LastScanTimeText = report.ScannedAtUtc.ToLocalTime().ToString("g");

                DuplicateSsidAnalyses.Clear();
                foreach (var d in report.DuplicateSsidAnalyses) DuplicateSsidAnalyses.Add(d);

                CongestionMetrics.Clear();
                foreach (var c in report.ChannelCongestion) CongestionMetrics.Add(c);

                ApplyFilterAndSortInternal();

                ScanStatusMessage = $"Scan completed. Discovered {TotalAccessPoints} access point(s). Security posture: {OverallPostureBadgeText}.";
            });

            // Persist scan session metadata to SQLite (privacy-filtered: no sensitive data)
            var session = new WifiScanSession
            {
                ScannedAtUtc = report.ScannedAtUtc,
                TotalNetworksFound = report.TotalAccessPoints,
                OverallPosture = report.OverallPosture,
                ObservationsJson = JsonSerializer.Serialize(scanResult.Observations),
                FindingsSummaryJson = JsonSerializer.Serialize(report.Findings)
            };
            await _repository.SaveScanSessionAsync(session);
            await LoadScanHistoryAsync();

            await _auditService.LogAsync(
                AuditAction.WifiSecurityScanCompleted,
                "LocalWifiAdapter",
                succeeded: true,
                $"Passive Wi-Fi security assessment completed. Discovered {report.TotalAccessPoints} access points, {report.Findings.Count} findings");
        }
        catch (OperationCanceledException)
        {
            RunOnUI(() =>
            {
                ScanStatusMessage = "Scan cancelled by operator.";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Wi-Fi security scan");
            RunOnUI(() =>
            {
                HasError = true;
                ErrorMessage = $"Error during Wi-Fi scan: {ex.Message}";
                ScanStatusMessage = "Scan failed.";
            });
        }
        finally
        {
            RunOnUI(() =>
            {
                IsScanning = false;
            });
        }
    }

    [RelayCommand]
    public void StopScan()
    {
        _scanCts?.Cancel();
        IsScanning = false;
        ScanStatusMessage = "Scan stopped by operator.";
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilterAndSort();
    partial void OnSelectedSeverityFilterChanged(string value) => ApplyFilterAndSort();
    partial void OnSelectedBandFilterChanged(string value) => ApplyFilterAndSort();
    partial void OnSelectedSortOptionChanged(string value) => ApplyFilterAndSort();

    public void ApplyFilterAndSort()
    {
        RunOnUI(ApplyFilterAndSortInternal);
    }

    private void ApplyFilterAndSortInternal()
    {
        var query = _allAccessPoints.AsEnumerable();

        // 1. Search Query
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            query = query.Where(a =>
                (a.Ssid != null && a.Ssid.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (a.Bssid != null && a.Bssid.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (a.RawSecurityString != null && a.RawSecurityString.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        // 2. Band Filter
        if (SelectedBandFilter == "6 GHz") query = query.Where(a => a.BandGroup == "6 GHz");
        else if (SelectedBandFilter == "5 GHz") query = query.Where(a => a.BandGroup == "5 GHz");
        else if (SelectedBandFilter == "2.4 GHz") query = query.Where(a => a.BandGroup == "2.4 GHz");

        // 3. Severity Filter
        if (SelectedSeverityFilter != "All" && _currentReport != null)
        {
            if (Enum.TryParse<FindingSeverity>(SelectedSeverityFilter, out var targetSeverity))
            {
                var apIdsWithSeverity = _currentReport.Findings
                    .Where(f => f.Severity == targetSeverity)
                    .Select(f => f.AccessPointId)
                    .ToHashSet();

                query = query.Where(a => apIdsWithSeverity.Contains(a.Id));
            }
        }

        // 4. Sort Option
        query = SelectedSortOption switch
        {
            "Signal Strength" => query.OrderByDescending(a => a.Rssi.HasValue).ThenByDescending(a => a.Rssi ?? -100),
            "SSID (A-Z)" => query.OrderBy(a => a.DisplaySsid, StringComparer.OrdinalIgnoreCase),
            "Channel" => query.OrderBy(a => a.Channel).ThenBy(a => a.DisplaySsid),
            _ => query.OrderBy(a => a.SecurityStandard == WifiSecurityStandard.Wep ? 0 :
                                   a.SecurityStandard == WifiSecurityStandard.Open ? 1 :
                                   a.SecurityStandard == WifiSecurityStandard.WpaPersonal ? 2 :
                                   a.SecurityStandard == WifiSecurityStandard.Wpa2Personal ? 3 : 4)
                      .ThenByDescending(a => a.Rssi ?? -100)
        };

        FilteredAccessPoints.Clear();
        foreach (var item in query)
        {
            FilteredAccessPoints.Add(item);
        }
    }

    partial void OnSelectedAccessPointChanged(WifiAccessPointObservation? value)
    {
        RunOnUI(() =>
        {
            if (value == null)
            {
                IsDetailDrawerOpen = false;
                SelectedApFindings.Clear();
                return;
            }

            SelectedApFindings.Clear();
            if (_currentReport != null)
            {
                var matchingFindings = _currentReport.Findings
                    .Where(f => f.AccessPointId == value.Id || (!string.IsNullOrEmpty(value.Ssid) && f.Ssid.Equals(value.Ssid, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                foreach (var f in matchingFindings)
                {
                    SelectedApFindings.Add(f);
                }
            }

            IsDetailDrawerOpen = true;
        });
    }

    [RelayCommand]
    public void OpenDetailDrawer(WifiAccessPointObservation? ap)
    {
        SelectedAccessPoint = ap;
    }

    [RelayCommand]
    public void CloseDetailDrawer()
    {
        IsDetailDrawerOpen = false;
        SelectedAccessPoint = null;
    }

    // --- Authorized Audit Mode Workflow ---

    [RelayCommand]
    public void OpenAuditModal(WifiAccessPointObservation? ap)
    {
        var target = ap ?? SelectedAccessPoint;
        if (target == null) return;

        AuditTargetSsid = target.DisplaySsid;
        AuditTargetBssid = target.Bssid;
        IsOwnershipConfirmed = false;
        AuditNotes = string.Empty;
        IsRouterVerified = false;

        AuditChecklistItems.Clear();
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "WPS Hardening",
            Title = "Wi-Fi Protected Setup (WPS) Disabled",
            Description = "WPS PIN / Push Button authentication must be disabled in router firmware.",
            VerificationGuide = "Router Admin > Wireless Settings > WPS > Toggle to Disabled / Off."
        });
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "Management Protection",
            Title = "Protected Management Frames (802.11w / PMF) Enforced",
            Description = "Enforces cryptographic protection for deauthentication and disassociation frames.",
            VerificationGuide = "Router Admin > Wireless Security > PMF / Management Frame Protection > Set to Required or Capable."
        });
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "Authentication",
            Title = "Modern WPA3-Personal (SAE) or WPA2-AES Encryption",
            Description = "Disallow legacy WEP and WPA1/TKIP; require strong passphrase.",
            VerificationGuide = "Router Admin > Wireless Security > Select WPA3-Personal or WPA2-AES."
        });
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "Access Control",
            Title = "Router Default Administrator Password Changed",
            Description = "Factory-default admin passwords (e.g. admin/admin) must be replaced.",
            VerificationGuide = "Router Admin > System / Administration > Change Admin Password."
        });
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "Network Segmentation",
            Title = "CCTV Cameras Partitioned on Dedicated VLAN / Guest Network",
            Description = "Camera devices should be isolated from primary personal/work computing devices.",
            VerificationGuide = "Router Admin > Guest Network / VLAN > Create isolated IoT network."
        });
        AuditChecklistItems.Add(new AuditChecklistItem
        {
            Category = "Firmware Integrity",
            Title = "Router Firmware Updated to Latest Security Release",
            Description = "Vendor security patches resolve known RCE and authentication bypass vulnerabilities.",
            VerificationGuide = "Router Admin > Firmware Upgrade > Check for Updates."
        });

        IsAuditModalOpen = true;
    }

    [RelayCommand]
    public void CloseAuditModal()
    {
        IsAuditModalOpen = false;
    }

    [RelayCommand]
    public async Task SaveAuditRecordAsync()
    {
        if (!IsOwnershipConfirmed)
        {
            ErrorMessage = "Operator must confirm network ownership or testing authorization.";
            HasError = true;
            return;
        }

        var record = new AuthorizedAuditRecord
        {
            Ssid = AuditTargetSsid,
            Bssid = AuditTargetBssid,
            AuditedAtUtc = DateTime.UtcNow,
            OwnershipConfirmed = true,
            Items = [.. AuditChecklistItems],
            OperatorNotes = AuditNotes,
            RouterVerified = IsRouterVerified
        };

        await _repository.SaveAuditRecordAsync(record).ConfigureAwait(false);

        await _auditService.LogAsync(
            AuditAction.WifiAuditRecordSaved,
            AuditTargetSsid,
            succeeded: true,
            $"Authorized audit record saved for SSID '{AuditTargetSsid}' (Verified: {IsRouterVerified})").ConfigureAwait(false);

        IsAuditModalOpen = false;
        ScanStatusMessage = $"Saved defensive audit verification for '{AuditTargetSsid}'.";
    }

    // --- Local Password Strength Tool (Zero Retention) ---

    [RelayCommand]
    public void OpenPasswordModal()
    {
        CandidatePasswordInput = string.Empty;
        PasswordEstimate = _passwordEstimator.Estimate(string.Empty);
        IsPasswordModalOpen = true;
    }

    partial void OnCandidatePasswordInputChanged(string value)
    {
        PasswordEstimate = _passwordEstimator.Estimate(value);
    }

    [RelayCommand]
    public void ClosePasswordModal()
    {
        // Zero-retention guarantee: immediately clear candidate memory
        CandidatePasswordInput = string.Empty;
        PasswordEstimate = null;
        IsPasswordModalOpen = false;
    }

    // --- Export Commands ---

    [RelayCommand]
    public async Task ExportJsonAsync()
    {
        try
        {
            var exportDto = new
            {
                CamScopeVersion = "1.0",
                ExportedAtUtc = DateTime.UtcNow,
                Report = _currentReport,
                DuplicateAnalyses = DuplicateSsidAnalyses,
                Congestion = CongestionMetrics
            };

            var json = JsonSerializer.Serialize(exportDto, new JsonSerializerOptions { WriteIndented = true });
            var filename = $"CamScope_WifiSecurity_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            var savePath = GetSafeExportPath(filename);

            await File.WriteAllTextAsync(savePath, json).ConfigureAwait(false);
            ExportFeedback = $"Saved security JSON report: {filename}";
        }
        catch (Exception ex)
        {
            ExportFeedback = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("SSID,BSSID,RSSI,SignalPercent,Channel,Band,PhyMode,SecurityStandard,WPS,PMF,FindingsCount");

            foreach (var ap in _allAccessPoints)
            {
                int findingsCount = _currentReport?.Findings.Count(f => f.AccessPointId == ap.Id) ?? 0;
                string cleanSsid = ap.DisplaySsid.Replace("\"", "\"\"");
                string bssid = ap.DisplayBssid.Replace("\"", "\"\"");

                sb.AppendLine($"\"{cleanSsid}\",\"{bssid}\",{ap.Rssi?.ToString() ?? "N/A"},{ap.SignalQualityPercent}%,{ap.Channel},\"{ap.Band}\",\"{ap.PhyMode}\",\"{ap.SecurityStandard}\",{ap.SupportsWps?.ToString() ?? "Unknown"},\"{ap.PmfCapability}\",{findingsCount}");
            }

            var filename = $"CamScope_WifiSecurity_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            var savePath = GetSafeExportPath(filename);

            await File.WriteAllTextAsync(savePath, sb.ToString()).ConfigureAwait(false);
            ExportFeedback = $"Saved security CSV report: {filename}";
        }
        catch (Exception ex)
        {
            ExportFeedback = $"Export error: {ex.Message}";
        }
    }

    private static string GetSafeExportPath(string filename)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            {
                return Path.Combine(desktop, filename);
            }
        }
        catch { }

        return Path.Combine(AppContext.BaseDirectory, filename);
    }

    private async Task LoadScanHistoryAsync()
    {
        try
        {
            var history = await _repository.GetScanHistoryAsync(20);
            RunOnUI(() =>
            {
                ScanHistory.Clear();
                foreach (var item in history) ScanHistory.Add(item);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load scan history from repository");
        }
    }

    [RelayCommand]
    public async Task ClearHistoryAsync()
    {
        try
        {
            await _repository.ClearScanHistoryAsync();
            RunOnUI(() =>
            {
                ScanHistory.Clear();
                ScanStatusMessage = "Scan history cleared.";
            });
        }
        catch (Exception ex)
        {
            RunOnUI(() =>
            {
                ExportFeedback = $"Failed to clear history: {ex.Message}";
            });
        }
    }

    private void RunOnUI(Action action)
    {
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private async Task RunOnUIAsync(Action action)
    {
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        CandidatePasswordInput = string.Empty;
        PasswordEstimate = null;
    }
}
