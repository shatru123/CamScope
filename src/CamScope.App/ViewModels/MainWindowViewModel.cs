using Avalonia;
using Avalonia.Styling;
using CamScope.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly OverviewViewModel _overviewVm;
    private readonly DiscoveryViewModel _discoveryVm;
    private readonly InventoryViewModel _inventoryVm;
    private readonly LiveMonitoringViewModel _liveVm;
    private readonly RecordingsViewModel _recordingsVm;
    private readonly NetworkInterfacesViewModel _interfacesVm;
    private readonly DeviceGroupsViewModel _groupsVm;
    private readonly DiagnosticsViewModel _diagnosticsVm;
    private readonly AuditLogViewModel _auditVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private int _selectedNavigationIndex;
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _selectedInterfaceText = "Auto (Default)";
    [ObservableProperty] private string _discoveryStateText = "Idle";
    [ObservableProperty] private bool _isDemoModeEnabled;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string? _notificationText;
    [ObservableProperty] private bool _hasNotification;

    public OverviewViewModel OverviewVm => _overviewVm;
    public DiscoveryViewModel DiscoveryVm => _discoveryVm;
    public InventoryViewModel InventoryVm => _inventoryVm;
    public LiveMonitoringViewModel LiveVm => _liveVm;
    public RecordingsViewModel RecordingsVm => _recordingsVm;
    public NetworkInterfacesViewModel InterfacesVm => _interfacesVm;
    public DeviceGroupsViewModel GroupsVm => _groupsVm;
    public DiagnosticsViewModel DiagnosticsVm => _diagnosticsVm;
    public AuditLogViewModel AuditVm => _auditVm;
    public SettingsViewModel SettingsVm => _settingsVm;

    public MainWindowViewModel(
        OverviewViewModel overviewVm,
        DiscoveryViewModel discoveryVm,
        InventoryViewModel inventoryVm,
        LiveMonitoringViewModel liveVm,
        RecordingsViewModel recordingsVm,
        NetworkInterfacesViewModel interfacesVm,
        DeviceGroupsViewModel groupsVm,
        DiagnosticsViewModel diagnosticsVm,
        AuditLogViewModel auditVm,
        SettingsViewModel settingsVm,
        ISettingsService settingsService)
    {
        _overviewVm = overviewVm;
        _discoveryVm = discoveryVm;
        _inventoryVm = inventoryVm;
        _liveVm = liveVm;
        _recordingsVm = recordingsVm;
        _interfacesVm = interfacesVm;
        _groupsVm = groupsVm;
        _diagnosticsVm = diagnosticsVm;
        _auditVm = auditVm;
        _settingsVm = settingsVm;
        _settingsService = settingsService;

        _currentPage = _overviewVm;

        // Initialize state
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadSettingsAsync();
        IsDemoModeEnabled = settings.IsDemoModeEnabled;

        if (_discoveryVm.SelectedInterface != null)
        {
            SelectedInterfaceText = $"{_discoveryVm.SelectedInterface.Name} ({_discoveryVm.SelectedInterface.IPv4Address})";
        }

        await _overviewVm.RefreshAsync();
    }

    partial void OnSelectedNavigationIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            0 => _overviewVm,
            1 => _discoveryVm,
            2 => _inventoryVm,
            3 => _liveVm,
            4 => _recordingsVm,
            5 => _interfacesVm,
            6 => _groupsVm,
            7 => _diagnosticsVm,
            8 => _auditVm,
            9 => _settingsVm,
            _ => _overviewVm
        };

        // Trigger view-specific refreshes
        if (value == 0) _ = _overviewVm.RefreshAsync();
        if (value == 2) _ = _inventoryVm.LoadDevicesAsync();
        if (value == 3) _ = _liveVm.LoadCamerasAsync();
        if (value == 4) _ = _recordingsVm.LoadDevicesAsync();
        if (value == 6) _ = _groupsVm.LoadGroupsAsync();
        if (value == 7) _diagnosticsVm.RefreshDiagnostics();
        if (value == 8) _ = _auditVm.LoadLogsAsync();
        if (value == 9) _ = _settingsVm.LoadSettingsAsync();
    }

    public void NavigateToIndex(int index)
    {
        SelectedNavigationIndex = index;
    }

    partial void OnIsDemoModeEnabledChanged(bool value)
    {
        ShowNotification(value
            ? "[DEMO MODE ENABLED] Simulated cameras will be used. External network sockets will not be scanned."
            : "[DEMO MODE DISABLED] Switched to genuine local network discovery.");
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    public void ShowNotification(string message)
    {
        NotificationText = message;
        HasNotification = true;
    }

    [RelayCommand]
    public void DismissNotification()
    {
        HasNotification = false;
        NotificationText = null;
    }
}
