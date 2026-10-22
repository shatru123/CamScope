using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CamScope.App.ViewModels;
using CamScope.App.Views;
using CamScope.Application.Security;
using CamScope.Application.Services;
using CamScope.CameraProtocols.Onvif;
using CamScope.Core.Interfaces;
using CamScope.Discovery.Engine;
using CamScope.Discovery.Scanning;
using CamScope.Infrastructure.Logging;
using CamScope.Infrastructure.Network;
using CamScope.Infrastructure.Security;
using CamScope.Persistence;
using CamScope.Persistence.Repositories;
using CamScope.Persistence.Services;
using CamScope.Streaming;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CamScope.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        // Initialize Database
        var dbInit = _serviceProvider.GetRequiredService<DatabaseInitializer>();
        dbInit.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Persistence (SQLite)
        var connectionString = DatabaseInitializer.CreateDefaultConnectionString();
        services.AddSingleton(new DatabaseInitializer(connectionString));
        services.AddSingleton<IDeviceRepository>(_ => new SqliteDeviceRepository(connectionString));
        services.AddSingleton<IAuditService>(_ => new SqliteAuditService(connectionString));
        services.AddSingleton<ISettingsService>(_ => new SqliteSettingsService(connectionString));
        services.AddSingleton<IWifiSecurityRepository>(_ => new SqliteWifiSecurityRepository(connectionString));

        // Network & Security
        services.AddSingleton<INetworkInterfaceProvider, NetworkInterfaceProvider>();
        services.AddSingleton<INetworkSelectionService, NetworkSelectionService>();
        services.AddSingleton<INetworkPermissionService, NetworkPermissionService>();
        services.AddSingleton<INetworkInventoryService, NetworkInventoryService>();
        services.AddSingleton<ISubnetScanner, SubnetScanner>();
        services.AddSingleton<ICredentialStore, SecureCredentialStore>();
        services.AddSingleton<IWifiSecurityAnalyzer, WifiSecurityAnalyzer>();
        services.AddSingleton<IPasswordStrengthEstimator, PasswordStrengthEstimator>();
        services.AddSingleton<INistPasswordAuditor, NistPasswordAuditor>();
        services.AddSingleton<IAdminAuthenticationController, AdminAuthenticationController>();

        // Wi-Fi Network Manager
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IMacCoreWlanConnector, MacCoreWlanConnector>();
            services.AddSingleton<IWifiNetworkManager, MacWifiNetworkManager>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IWifiNetworkManager, LinuxWifiNetworkManager>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IWifiNetworkManager, WindowsWifiNetworkManager>();
        }
        else
        {
            services.AddSingleton<IWifiNetworkManager, UnsupportedPlatformWifiNetworkManager>();
        }

        // Wi-Fi Security Scanner
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IWifiSecurityScanner, MacWifiSecurityScanner>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IWifiSecurityScanner, WindowsWifiSecurityScanner>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IWifiSecurityScanner, LinuxWifiSecurityScanner>();
        }
        else
        {
            services.AddSingleton<IWifiSecurityScanner, UnsupportedWifiSecurityScanner>();
        }

        // Discovery Engines
        services.AddSingleton<WsDiscoveryEngine>();
        services.AddSingleton<DemoDiscoveryEngine>();

        // ONVIF Client
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(8) });
        services.AddSingleton<IOnvifClient, OnvifDeviceClient>();

        // Streaming Service
        services.AddSingleton<IStreamingService, StreamingService>();

        // Application Services
        services.AddSingleton<DeviceManagementService>();

        // ViewModels
        var isDemoModeHolder = new DemoModeState();
        services.AddSingleton(isDemoModeHolder);

        services.AddSingleton<OverviewViewModel>(sp => new OverviewViewModel(
            sp.GetRequiredService<IDeviceRepository>(),
            sp.GetRequiredService<IAuditService>(),
            idx => sp.GetRequiredService<MainWindowViewModel>().NavigateToIndex(idx)));

        services.AddSingleton<DiscoveryViewModel>(sp => new DiscoveryViewModel(
            sp.GetRequiredService<WsDiscoveryEngine>(),
            sp.GetRequiredService<DemoDiscoveryEngine>(),
            sp.GetRequiredService<INetworkInterfaceProvider>(),
            sp.GetRequiredService<IDeviceRepository>(),
            sp.GetRequiredService<IAuditService>(),
            () => isDemoModeHolder.IsDemoMode,
            sp.GetRequiredService<INetworkSelectionService>()));

        services.AddSingleton<InventoryViewModel>(sp => new InventoryViewModel(
            sp.GetRequiredService<IDeviceRepository>(),
            sp.GetRequiredService<DeviceManagementService>(),
            () => isDemoModeHolder.IsDemoMode));

        services.AddSingleton<LiveMonitoringViewModel>();
        services.AddSingleton<RecordingsViewModel>(sp => new RecordingsViewModel(
            sp.GetRequiredService<IDeviceRepository>(),
            sp.GetRequiredService<IOnvifClient>(),
            sp.GetRequiredService<ICredentialStore>(),
            () => isDemoModeHolder.IsDemoMode));

        services.AddSingleton<NetworkExplorerViewModel>();
        services.AddSingleton<WifiSecurityViewModel>();
        services.AddSingleton<NetworkInterfacesViewModel>();
        services.AddSingleton<DeviceGroupsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<AuditLogViewModel>();
        services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsService>(),
            enabled =>
            {
                isDemoModeHolder.IsDemoMode = enabled;
                var main = sp.GetRequiredService<MainWindowViewModel>();
                main.IsDemoModeEnabled = enabled;
            }));

        services.AddSingleton<MainWindowViewModel>(sp => new MainWindowViewModel(
            sp.GetRequiredService<OverviewViewModel>(),
            sp.GetRequiredService<DiscoveryViewModel>(),
            sp.GetRequiredService<InventoryViewModel>(),
            sp.GetRequiredService<LiveMonitoringViewModel>(),
            sp.GetRequiredService<RecordingsViewModel>(),
            sp.GetRequiredService<NetworkExplorerViewModel>(),
            sp.GetRequiredService<WifiSecurityViewModel>(),
            sp.GetRequiredService<DeviceGroupsViewModel>(),
            sp.GetRequiredService<DiagnosticsViewModel>(),
            sp.GetRequiredService<AuditLogViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<INetworkSelectionService>()));
    }
}

public sealed class DemoModeState
{
    public bool IsDemoMode { get; set; }
}
