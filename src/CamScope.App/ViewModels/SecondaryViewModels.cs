using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class NetworkInterfacesViewModel : ViewModelBase
{
    private readonly INetworkInterfaceProvider _provider;
    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = [];
    [ObservableProperty] private NetworkInterfaceInfo? _selectedInterface;

    public NetworkInterfacesViewModel(INetworkInterfaceProvider provider)
    {
        _provider = provider;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Interfaces.Clear();
        foreach (var i in _provider.GetEligibleInterfaces())
        {
            Interfaces.Add(i);
        }
        SelectedInterface = Interfaces.FirstOrDefault();
    }
}

public sealed partial class DeviceGroupsViewModel : ViewModelBase
{
    private readonly IDeviceRepository _deviceRepository;

    public ObservableCollection<GroupingNode> Groupings { get; } = [];

    public DeviceGroupsViewModel(IDeviceRepository deviceRepository)
    {
        _deviceRepository = deviceRepository;
    }

    [RelayCommand]
    public async Task LoadGroupsAsync()
    {
        var devices = await _deviceRepository.GetAllAsync();
        Groupings.Clear();

        // Group by Location
        var locationRoot = new GroupingNode("By Location");
        var locations = devices.GroupBy(d => string.IsNullOrWhiteSpace(d.Location) ? "Unassigned Location" : d.Location);
        foreach (var loc in locations)
        {
            var locNode = new GroupingNode($"{loc.Key} ({loc.Count()})");
            foreach (var d in loc)
            {
                locNode.Children.Add(new GroupingNode($"{d.DisplayName} [{d.Host}]"));
            }
            locationRoot.Children.Add(locNode);
        }
        Groupings.Add(locationRoot);

        // Group by Vendor
        var vendorRoot = new GroupingNode("By Vendor");
        var vendors = devices.GroupBy(d => d.Vendor);
        foreach (var v in vendors)
        {
            var vNode = new GroupingNode($"{v.Key} ({v.Count()})");
            foreach (var d in v)
            {
                vNode.Children.Add(new GroupingNode($"{d.DisplayName} [{d.Host}]"));
            }
            vendorRoot.Children.Add(vNode);
        }
        Groupings.Add(vendorRoot);
    }
}

public sealed class GroupingNode
{
    public string Name { get; set; }
    public ObservableCollection<GroupingNode> Children { get; } = [];

    public GroupingNode(string name)
    {
        Name = name;
    }
}

public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    [ObservableProperty] private string _osArchitecture = RuntimeInformation.OSArchitecture.ToString();
    [ObservableProperty] private string _osDescription = RuntimeInformation.OSDescription;
    [ObservableProperty] private string _frameworkDescription = RuntimeInformation.FrameworkDescription;
    [ObservableProperty] private string _databasePath = DatabaseInitializer.GetDefaultDbPath();
    [ObservableProperty] private string _memoryUsageMb = "0 MB";
    [ObservableProperty] private string _uptime = "0s";
    [ObservableProperty] private string _networkStatus = "Sockets Available (UDP Multicast & TCP)";

    private readonly Stopwatch _uptimeWatch = Stopwatch.StartNew();

    public DiagnosticsViewModel()
    {
        RefreshDiagnostics();
    }

    [RelayCommand]
    public void RefreshDiagnostics()
    {
        var process = Process.GetCurrentProcess();
        MemoryUsageMb = $"{process.WorkingSet64 / (1024 * 1024)} MB";
        Uptime = $"{_uptimeWatch.Elapsed.Hours}h {_uptimeWatch.Elapsed.Minutes}m {_uptimeWatch.Elapsed.Seconds}s";
    }
}

public sealed partial class AuditLogViewModel : ViewModelBase
{
    private readonly IAuditService _auditService;
    public ObservableCollection<ActivityAuditEntry> Logs { get; } = [];
    [ObservableProperty] private string? _feedbackMessage;

    public AuditLogViewModel(IAuditService auditService)
    {
        _auditService = auditService;
    }

    [RelayCommand]
    public async Task LoadLogsAsync()
    {
        Logs.Clear();
        var entries = await _auditService.GetRecentLogsAsync(200);
        foreach (var entry in entries)
        {
            Logs.Add(entry);
        }
    }

    [RelayCommand]
    public async Task ClearLogsAsync()
    {
        await _auditService.ClearLogsAsync();
        Logs.Clear();
        FeedbackMessage = "Audit log cleared.";
    }

    [RelayCommand]
    public async Task ExportAuditLogAsync()
    {
        try
        {
            var entries = await _auditService.GetRecentLogsAsync(1000);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var filename = $"CamScope_AuditLog_{timestamp}.csv";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), filename);

            using var sw = new StreamWriter(path);
            sw.WriteLine("Id,TimestampUtc,Action,TargetEndpoint,UserOrSource,Succeeded,Details");
            foreach (var e in entries)
            {
                sw.WriteLine($"\"{e.Id}\",\"{e.TimestampUtc:o}\",\"{e.Action}\",\"{e.TargetEndpoint}\",\"{e.UserOrSource}\",{e.Succeeded},\"{e.Details.Replace("\"", "\"\"")}\"");
            }
            FeedbackMessage = $"Saved audit log to Desktop: {filename}";
        }
        catch (Exception ex)
        {
            FeedbackMessage = $"Export error: {ex.Message}";
        }
    }
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly Action<bool> _onDemoModeChanged;

    [ObservableProperty] private bool _isDemoModeEnabled;
    [ObservableProperty] private int _discoveryTimeoutSeconds = 6;
    [ObservableProperty] private int _maxParallelStreams = 4;
    [ObservableProperty] private bool _autoReconnectStreams = true;
    [ObservableProperty] private int _retainAuditDays = 30;
    [ObservableProperty] private string? _feedbackMessage;

    public SettingsViewModel(ISettingsService settingsService, Action<bool> onDemoModeChanged)
    {
        _settingsService = settingsService;
        _onDemoModeChanged = onDemoModeChanged;
    }

    public async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadSettingsAsync();
        IsDemoModeEnabled = settings.IsDemoModeEnabled;
        DiscoveryTimeoutSeconds = settings.DiscoveryTimeoutSeconds;
        MaxParallelStreams = settings.MaxParallelStreams;
        AutoReconnectStreams = settings.AutoReconnectStreams;
        RetainAuditDays = settings.RetainAuditDays;
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        var settings = new ApplicationSettings
        {
            IsDemoModeEnabled = IsDemoModeEnabled,
            DiscoveryTimeoutSeconds = DiscoveryTimeoutSeconds,
            MaxParallelStreams = MaxParallelStreams,
            AutoReconnectStreams = AutoReconnectStreams,
            RetainAuditDays = RetainAuditDays
        };

        await _settingsService.SaveSettingsAsync(settings);
        _onDemoModeChanged(IsDemoModeEnabled);
        FeedbackMessage = "Settings saved successfully.";
    }

    partial void OnIsDemoModeEnabledChanged(bool value)
    {
        _onDemoModeChanged(value);
    }
}
