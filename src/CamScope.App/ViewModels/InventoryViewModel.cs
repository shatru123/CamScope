using System.Collections.ObjectModel;
using CamScope.Application.Services;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CamScope.App.ViewModels;

public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly DeviceManagementService _deviceService;
    private readonly Func<bool> _isDemoModeFunc;

    private readonly List<CameraDevice> _allDevices = [];

    public ObservableCollection<CameraDevice> FilteredDevices { get; } = [];

    [ObservableProperty] private CameraDevice? _selectedDevice;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedVendorFilter = "All Vendors";
    [ObservableProperty] private string _selectedStatusFilter = "All Statuses";
    [ObservableProperty] private bool _isCardView;
    [ObservableProperty] private bool _isLoading;

    // Authentication Dialog State
    [ObservableProperty] private bool _isAuthDialogOpen;
    [ObservableProperty] private string _authUsername = "admin";
    [ObservableProperty] private string _authPassword = string.Empty;
    [ObservableProperty] private string? _authError;
    [ObservableProperty] private bool _isAuthenticating;

    // Add Manual Device Dialog State
    [ObservableProperty] private bool _isAddDialogOpen;
    [ObservableProperty] private string _manualHost = "192.168.1.";
    [ObservableProperty] private int _manualPort = 80;
    [ObservableProperty] private string _manualPath = "/onvif/device_service";
    [ObservableProperty] private string _manualUsername = "";
    [ObservableProperty] private string _manualPassword = "";
    [ObservableProperty] private string? _addError;
    [ObservableProperty] private bool _isAdding;

    // Export Feedback
    [ObservableProperty] private string? _exportFeedback;

    public ObservableCollection<string> VendorFilters { get; } = ["All Vendors"];
    public ObservableCollection<string> StatusFilters { get; } =
    [
        "All Statuses",
        "Authenticated",
        "Unauthenticated",
        "Connected",
        "Offline"
    ];

    public InventoryViewModel(
        IDeviceRepository deviceRepository,
        DeviceManagementService deviceService,
        Func<bool> isDemoModeFunc)
    {
        _deviceRepository = deviceRepository;
        _deviceService = deviceService;
        _isDemoModeFunc = isDemoModeFunc;
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _deviceRepository.GetAllAsync();

            if (list.Count == 0 && _isDemoModeFunc())
            {
                // In demo mode with empty DB, seed demo devices
                foreach (var demo in DemoDataGenerator.CreateDemoDevices())
                {
                    await _deviceRepository.AddOrUpdateAsync(demo);
                }
                list = await _deviceRepository.GetAllAsync();
            }

            _allDevices.Clear();
            _allDevices.AddRange(list);

            RefreshVendorFilters();
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSelectedVendorFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilter();

    private void RefreshVendorFilters()
    {
        var current = SelectedVendorFilter;
        VendorFilters.Clear();
        VendorFilters.Add("All Vendors");

        var vendors = _allDevices
            .Select(d => d.Vendor)
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown")
            .Distinct()
            .OrderBy(v => v);

        foreach (var v in vendors)
        {
            VendorFilters.Add(v);
        }

        SelectedVendorFilter = VendorFilters.Contains(current) ? current : "All Vendors";
    }

    public void ApplyFilter()
    {
        var filtered = _allDevices.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            filtered = filtered.Where(d =>
                d.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Model.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (d.Location != null && d.Location.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        if (SelectedVendorFilter != "All Vendors")
        {
            filtered = filtered.Where(d => d.Vendor.Equals(SelectedVendorFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedStatusFilter == "Authenticated")
        {
            filtered = filtered.Where(d => d.AuthState == AuthenticationState.Authenticated);
        }
        else if (SelectedStatusFilter == "Unauthenticated")
        {
            filtered = filtered.Where(d => d.AuthState != AuthenticationState.Authenticated);
        }
        else if (SelectedStatusFilter == "Connected")
        {
            filtered = filtered.Where(d => d.ConnectionState == DeviceConnectionState.Connected);
        }
        else if (SelectedStatusFilter == "Offline")
        {
            filtered = filtered.Where(d => d.ConnectionState == DeviceConnectionState.Unavailable || d.ConnectionState == DeviceConnectionState.Error);
        }

        FilteredDevices.Clear();
        foreach (var d in filtered)
        {
            FilteredDevices.Add(d);
        }

        if (SelectedDevice != null && !FilteredDevices.Contains(SelectedDevice))
        {
            SelectedDevice = FilteredDevices.FirstOrDefault();
        }
    }

    [RelayCommand]
    public void OpenAuthDialog(CameraDevice? device)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        SelectedDevice = target;
        AuthUsername = "admin";
        AuthPassword = string.Empty;
        AuthError = null;
        IsAuthDialogOpen = true;
    }

    [RelayCommand]
    public void CloseAuthDialog()
    {
        IsAuthDialogOpen = false;
        AuthPassword = string.Empty;
        AuthError = null;
    }

    [RelayCommand]
    public async Task SubmitAuthenticationAsync()
    {
        if (SelectedDevice == null) return;
        if (string.IsNullOrWhiteSpace(AuthUsername))
        {
            AuthError = "Username is required.";
            return;
        }

        IsAuthenticating = true;
        AuthError = null;

        try
        {
            var success = await _deviceService.AuthenticateDeviceAsync(
                SelectedDevice,
                AuthUsername,
                AuthPassword);

            if (success)
            {
                IsAuthDialogOpen = false;
                AuthPassword = string.Empty;
                await LoadDevicesAsync();
            }
            else
            {
                AuthError = SelectedDevice.LastError ?? "Authentication failed. Check credentials or network connectivity.";
            }
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    [RelayCommand]
    public void OpenAddDialog()
    {
        ManualHost = "192.168.1.";
        ManualPort = 80;
        ManualPath = "/onvif/device_service";
        ManualUsername = "";
        ManualPassword = "";
        AddError = null;
        IsAddDialogOpen = true;
    }

    [RelayCommand]
    public void CloseAddDialog()
    {
        IsAddDialogOpen = false;
        ManualPassword = "";
        AddError = null;
    }

    [RelayCommand]
    public async Task SubmitAddManualDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualHost))
        {
            AddError = "Host / IP address is required.";
            return;
        }

        IsAdding = true;
        AddError = null;

        try
        {
            var dev = await _deviceService.AddManualDeviceAsync(
                ManualHost.Trim(),
                ManualPort,
                ManualPath.Trim(),
                string.IsNullOrWhiteSpace(ManualUsername) ? null : ManualUsername.Trim(),
                string.IsNullOrWhiteSpace(ManualPassword) ? null : ManualPassword);

            IsAddDialogOpen = false;
            ManualPassword = "";
            await LoadDevicesAsync();
            SelectedDevice = FilteredDevices.FirstOrDefault(d => d.Id == dev.Id);
        }
        catch (Exception ex)
        {
            AddError = ex.Message;
        }
        finally
        {
            IsAdding = false;
        }
    }

    [RelayCommand]
    public async Task DeleteDeviceAsync(CameraDevice? device)
    {
        var target = device ?? SelectedDevice;
        if (target == null) return;

        await _deviceService.DeleteDeviceAsync(target.Id);
        await LoadDevicesAsync();
    }

    [RelayCommand]
    public async Task ExportReportAsync(string format)
    {
        try
        {
            var content = await _deviceService.ExportInventoryReportAsync(format);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var filename = $"CamScope_Inventory_{timestamp}.{format.ToLowerInvariant()}";
            var savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), filename);

            await File.WriteAllTextAsync(savePath, content);
            ExportFeedback = $"Saved report to Desktop: {filename}";
        }
        catch (Exception ex)
        {
            ExportFeedback = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ClearAllDevicesAsync()
    {
        await _deviceRepository.ClearAllAsync();
        await LoadDevicesAsync();
    }
}
