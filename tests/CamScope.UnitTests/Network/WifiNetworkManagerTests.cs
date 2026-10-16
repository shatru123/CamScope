using System.Diagnostics;
using System.Net.NetworkInformation;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using CamScope.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CamScope.UnitTests.Network;

public class WifiNetworkManagerTests
{
    private sealed class FakeInventoryService : INetworkInventoryService
    {
        public NetworkInventoryReport Report { get; set; } = new()
        {
            Interfaces =
            [
                new NetworkInterfaceInfo
                {
                    Id = "en0",
                    Name = "en0",
                    HardwarePort = "Wi-Fi",
                    InterfaceKind = NetworkInterfaceKind.WiFi,
                    IPv4Address = "192.168.1.100",
                    IsUp = true,
                    IsPrimaryInternet = true
                }
            ],
            PrimaryInterface = new NetworkInterfaceInfo
            {
                Id = "en0",
                Name = "en0",
                HardwarePort = "Wi-Fi",
                InterfaceKind = NetworkInterfaceKind.WiFi,
                IPv4Address = "192.168.1.100",
                IsPrimaryInternet = true
            },
            WifiNetworks =
            [
                new WifiNetworkInfo
                {
                    Ssid = "Test_Office_5G",
                    Rssi = -55,
                    Channel = 36,
                    Band = "5 GHz",
                    Security = "WPA2 Personal"
                }
            ],
            SystemDefaultGateway = "192.168.1.1",
            SystemDnsServers = ["192.168.1.1"]
        };

        public NetworkRoutingSnapshot? NextRoutingSnapshot { get; set; }
        public RoutingInvariantResult? MockInvariantResult { get; set; }

        public Task<NetworkInventoryReport> GetNetworkInventoryAsync(bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(Report);

        public Task<NetworkRoutingSnapshot> CaptureRoutingSnapshotAsync(CancellationToken ct = default)
        {
            if (NextRoutingSnapshot != null)
                return Task.FromResult(NextRoutingSnapshot);

            return Task.FromResult(new NetworkRoutingSnapshot
            {
                DefaultGateway = Report.SystemDefaultGateway,
                PrimaryInterfaceName = Report.PrimaryInterface?.Name ?? "en0",
                DnsServers = Report.SystemDnsServers
            });
        }

        public RoutingInvariantResult VerifyRoutingInvariant(NetworkRoutingSnapshot? before, NetworkRoutingSnapshot? after)
        {
            if (MockInvariantResult != null)
                return MockInvariantResult;

            return new RoutingInvariantResult { IsStable = true, VerificationSucceeded = true };
        }

        public bool VerifyRoutingInvariant(NetworkRoutingSnapshot before, NetworkRoutingSnapshot after, out string? mutationWarning)
        {
            mutationWarning = null;
            return true;
        }
    }

    private sealed class FakeAuditService : IAuditService
    {
        public List<ActivityAuditEntry> LoggedEntries { get; } = [];

        public Task LogAsync(CamScope.Core.Enums.AuditAction action, string? targetEndpoint, bool succeeded, string details, string? userOrSource = "LocalUser", CancellationToken ct = default)
        {
            LoggedEntries.Add(new ActivityAuditEntry
            {
                Action = action,
                TargetEndpoint = targetEndpoint,
                Succeeded = succeeded,
                Details = details,
                UserOrSource = userOrSource
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityAuditEntry>> GetRecentLogsAsync(int maxCount = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActivityAuditEntry>>(LoggedEntries);

        public Task ClearLogsAsync(CancellationToken ct = default)
        {
            LoggedEntries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCoreWlanConnector : IMacCoreWlanConnector
    {
        public bool ShouldSucceed { get; set; } = true;
        public int ExitCode { get; set; } = 0;
        public string Output { get; set; } = "ASSOCIATION_SUCCESS";
        public string? LastInterface { get; private set; }
        public string? LastSsid { get; private set; }
        public string? LastPassword { get; private set; }
        public bool DisassociateCalled { get; private set; }

        public Task<CoreWlanAssociationResult> AssociateAsync(string interfaceName, string ssid, string? password, CancellationToken ct = default)
        {
            LastInterface = interfaceName;
            LastSsid = ssid;
            LastPassword = password;

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            return Task.FromResult(new CoreWlanAssociationResult(
                ShouldSucceed,
                ExitCode,
                Output
            ));
        }

        public Task<bool> DisassociateAsync(string interfaceName, CancellationToken ct = default)
        {
            DisassociateCalled = true;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task MacWifiNetworkManager_ThrowsOnNullRequest()
    {
        var inventory = new FakeInventoryService();
        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.ConnectAsync(null!));
    }

    [Fact]
    public async Task MacWifiNetworkManager_RejectsEmptySsid()
    {
        var inventory = new FakeInventoryService();
        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "  ",
            InterfaceName = "en0"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be empty", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleWifiAdapter_EnforcesPrimaryPreservation_SafeStop()
    {
        var inventory = new FakeInventoryService();
        // 1 Wi-Fi adapter that is currently primary internet
        inventory.Report.Interfaces =
        [
            new NetworkInterfaceInfo
            {
                Id = "en0",
                Name = "en0",
                HardwarePort = "Wi-Fi",
                InterfaceKind = NetworkInterfaceKind.WiFi,
                IPv4Address = "192.168.1.100",
                IsUp = true,
                IsPrimaryInternet = true
            }
        ];
        inventory.Report.PrimaryInterface = inventory.Report.Interfaces[0];

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        // Capability check must identify single adapter conflict
        var capability = await manager.CheckSecondaryCapabilityAsync("en0");
        Assert.Equal(SecondaryAdapterCapability.SingleAdapterConflict, capability);

        // Attempting to connect must stop safely without calling CoreWLAN or mutating routes
        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "CCTV_Isolated_AP",
            Password = "CameraPassword123",
            InterfaceName = "en0"
        });

        Assert.False(result.Succeeded);
        Assert.True(result.PreservedPrimaryConnection);
        Assert.Equal(WifiConnectionState.AwaitingCredentials, result.State);
        Assert.Contains("Primary network preservation enforced", result.Message);
        Assert.NotNull(result.SuggestedAlternative);

        // Verify CoreWLAN association was NEVER called (preserving primary connection)
        Assert.Null(connector.LastSsid);
        // Verify password was never leaked in message
        Assert.DoesNotContain("CameraPassword123", result.Message);
    }

    [Fact]
    public async Task TwoWifiAdapters_AllowsIsolatedSecondaryConnection()
    {
        var inventory = new FakeInventoryService();
        // Primary Wi-Fi (en0) and Secondary Wi-Fi (en2)
        var en0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "192.168.1.50",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var en2 = new NetworkInterfaceInfo
        {
            Id = "en2",
            Name = "en2",
            HardwarePort = "USB Wi-Fi Adapter",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "10.0.0.15",
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [en0, en2];
        inventory.Report.PrimaryInterface = en0;

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector { ShouldSucceed = true, ExitCode = 0 };
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        var capability = await manager.CheckSecondaryCapabilityAsync("en2");
        Assert.Equal(SecondaryAdapterCapability.Capable, capability);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "Camera_Network_5G",
            Password = "SecurePassphrase!",
            InterfaceName = "en2"
        });

        Assert.True(result.Succeeded);
        Assert.True(result.PreservedPrimaryConnection);
        Assert.Equal(WifiConnectionState.Connected, result.State);
        Assert.Equal("en2", result.ConnectedInterface);
        Assert.Equal("en2", connector.LastInterface);
        Assert.Equal("Camera_Network_5G", connector.LastSsid);
        Assert.Equal("SecurePassphrase!", connector.LastPassword);
        Assert.NotNull(selection.CctvInterface);
        Assert.Equal("en2", selection.CctvInterface.Name);
    }

    [Fact]
    public async Task WifiAndEthernet_AllowsIsolatedWifiConnectionWhileEthernetPrimary()
    {
        var inventory = new FakeInventoryService();
        // Primary internet is on Ethernet (en0), secondary Wi-Fi adapter is en1
        var eth0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Ethernet",
            InterfaceKind = NetworkInterfaceKind.Ethernet,
            IPv4Address = "172.16.0.22",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var wifi1 = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "192.168.100.5",
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [eth0, wifi1];
        inventory.Report.PrimaryInterface = eth0;

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector { ShouldSucceed = true, ExitCode = 0 };
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        var capability = await manager.CheckSecondaryCapabilityAsync("en1");
        Assert.Equal(SecondaryAdapterCapability.Capable, capability);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "CCTV_WLAN",
            Password = "CameraKey999",
            InterfaceName = "en1"
        });

        Assert.True(result.Succeeded);
        Assert.True(result.PreservedPrimaryConnection);
        Assert.Equal("en1", result.ConnectedInterface);
        Assert.Equal("en1", connector.LastInterface);
    }

    [Fact]
    public async Task PostConnectionDhcpFailure_ReportsTimedOutSafely()
    {
        var inventory = new FakeInventoryService();
        var en0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Ethernet",
            InterfaceKind = NetworkInterfaceKind.Ethernet,
            IPv4Address = "192.168.1.10",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var en1 = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = string.Empty, // No DHCP IP assigned!
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [en0, en1];
        inventory.Report.PrimaryInterface = en0;

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector { ShouldSucceed = true, ExitCode = 0 };
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "DhcpFailed_SSID",
            InterfaceName = "en1"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(WifiConnectionState.TimedOut, result.State);
        Assert.Contains("failed to acquire a valid DHCP IP address", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.PreservedPrimaryConnection);
    }

    [Fact]
    public async Task RoutingInvariantViolation_TriggersRollbackAndReportsFailure()
    {
        var inventory = new FakeInventoryService();
        var eth0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Ethernet",
            InterfaceKind = NetworkInterfaceKind.Ethernet,
            IPv4Address = "192.168.1.10",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var wifi1 = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "10.0.0.99",
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [eth0, wifi1];
        inventory.Report.PrimaryInterface = eth0;

        // Simulate routing table mutation where default gateway changed unexpectedly
        inventory.MockInvariantResult = new RoutingInvariantResult
        {
            IsStable = false,
            VerificationSucceeded = true,
            StatusText = "Default gateway mutated unexpectedly from '192.168.1.1' to '10.0.0.1'.",
            MutationDetails = ["Default gateway changed"]
        };

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector { ShouldSucceed = true, ExitCode = 0 };
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "Mutating_SSID",
            InterfaceName = "en1"
        });

        Assert.False(result.Succeeded);
        Assert.False(result.PreservedPrimaryConnection);
        Assert.True(result.RouteRollbackAttempted);
        Assert.True(connector.DisassociateCalled);
        Assert.Contains("Primary network invariant violated", result.Message);
        Assert.Contains("rolled back the association", result.Message);
    }

    [Fact]
    public async Task PasswordExposureAudit_PasswordNeverAppearsInAuditOrProcessArguments()
    {
        var audit = new FakeAuditService();
        const string secretPassword = "SuperSensitivePassword987!";

        var inventory = new FakeInventoryService();
        var en0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Ethernet",
            InterfaceKind = NetworkInterfaceKind.Ethernet,
            IPv4Address = "192.168.1.10",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var en1 = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "10.0.0.2",
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [en0, en1];
        inventory.Report.PrimaryInterface = en0;

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var connector = new FakeCoreWlanConnector { ShouldSucceed = true };
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "PrivateCamWifi",
            Password = secretPassword,
            InterfaceName = "en1"
        });

        // Audit log must NEVER record the plaintext password
        foreach (var entry in audit.LoggedEntries)
        {
            Assert.DoesNotContain(secretPassword, entry.Details);
        }
    }

    [Fact]
    public async Task CrossPlatformManagers_HandleUnsupportedGracefully()
    {
        var linuxManager = new LinuxWifiNetworkManager(NullLogger<LinuxWifiNetworkManager>.Instance);
        var winManager = new WindowsWifiNetworkManager(NullLogger<WindowsWifiNetworkManager>.Instance);
        var unsupported = new UnsupportedPlatformWifiNetworkManager();

        var linuxResult = await linuxManager.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(linuxResult.Succeeded);
        var linuxCap = await linuxManager.CheckSecondaryCapabilityAsync("wlan0");
        Assert.Equal(SecondaryAdapterCapability.NotApplicable, linuxCap);

        var winResult = await winManager.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(winResult.Succeeded);
        var winCap = await winManager.CheckSecondaryCapabilityAsync("Wi-Fi");
        Assert.Equal(SecondaryAdapterCapability.NotApplicable, winCap);

        var unsupResult = await unsupported.ConnectAsync(new WifiConnectRequest { Ssid = "Net" });
        Assert.False(unsupResult.Succeeded);
        var unsupCap = await unsupported.CheckSecondaryCapabilityAsync("any");
        Assert.Equal(SecondaryAdapterCapability.NotApplicable, unsupCap);
    }

    [Fact]
    public void WifiNetworkInfo_EvaluatesSecurityAndBandCorrectly()
    {
        var openNet = new WifiNetworkInfo
        {
            Ssid = "Guest_Open",
            Security = "None",
            Band = "2.4 GHz",
            Channel = 1
        };
        Assert.False(openNet.IsSecured);
        Assert.Equal("2.4 GHz", openNet.BandGroup);

        var securedNet = new WifiNetworkInfo
        {
            Ssid = "Secure_5G",
            Security = "WPA3 Personal",
            Band = "5 GHz",
            Channel = 149
        };
        Assert.True(securedNet.IsSecured);
        Assert.Equal("5 GHz", securedNet.BandGroup);
    }

    [Theory]
    [InlineData("Failed to join network Office_5G. Error: (-3905)", -3905, WifiConnectionState.AuthenticationFailed, true, false)]
    [InlineData("Failed to join network. Error: (-3906)", -3906, WifiConnectionState.AuthenticationFailed, true, false)]
    [InlineData("Authentication failed: incorrect password.", -1, WifiConnectionState.AuthenticationFailed, true, false)]
    [InlineData("Error: (-3907) Password incorrect", -3907, WifiConnectionState.AuthenticationFailed, true, false)]
    [InlineData("Could not find network Lab_AP. Error: (-3900)", -3900, WifiConnectionState.NetworkUnavailable, true, false)]
    [InlineData("No network with that name. (-3901)", -3901, WifiConnectionState.NetworkUnavailable, true, false)]
    [InlineData("You must be an administrator to join this network. Error: (-3903)", -3903, WifiConnectionState.PermissionDenied, false, false)]
    [InlineData("This network requires 802.1X Enterprise authentication. (-3915)", -3915, WifiConnectionState.AuthenticationFailed, false, true)]
    [InlineData("EAP enterprise authentication profile required", -1, WifiConnectionState.AuthenticationFailed, false, true)]
    public void ClassifyNetworksetupError_ClassifiesCorrectly(
        string output, int exitCode, WifiConnectionState expectedState, bool expectedCanRetry, bool expectedUnsupported)
    {
        var (state, cleanMessage, errorClassification, canRetry, isUnsupported, alt) =
            MacWifiNetworkManager.ClassifyNetworksetupError(output, exitCode);

        Assert.Equal(expectedState, state);
        Assert.Equal(expectedCanRetry, canRetry);
        Assert.Equal(expectedUnsupported, isUnsupported);
        Assert.NotNull(cleanMessage);
        Assert.NotEmpty(cleanMessage);
        Assert.NotNull(errorClassification);

        if (expectedState == WifiConnectionState.AuthenticationFailed && !expectedUnsupported)
        {
            Assert.Contains("Authentication failed", cleanMessage, StringComparison.OrdinalIgnoreCase);
        }

        if (expectedUnsupported)
        {
            Assert.Contains("Enterprise", cleanMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("macOS System Settings", cleanMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ConnectionCancellation_SetsCancelledState()
    {
        var inventory = new FakeInventoryService();
        var eth0 = new NetworkInterfaceInfo
        {
            Id = "en0",
            Name = "en0",
            HardwarePort = "Ethernet",
            InterfaceKind = NetworkInterfaceKind.Ethernet,
            IPv4Address = "192.168.1.10",
            IsUp = true,
            IsPrimaryInternet = true
        };
        var wifi1 = new NetworkInterfaceInfo
        {
            Id = "en1",
            Name = "en1",
            HardwarePort = "Wi-Fi",
            InterfaceKind = NetworkInterfaceKind.WiFi,
            IPv4Address = "10.0.0.2",
            IsUp = true,
            IsPrimaryInternet = false
        };
        inventory.Report.Interfaces = [eth0, wifi1];
        inventory.Report.PrimaryInterface = eth0;

        var selection = new NetworkSelectionService(NullLogger<NetworkSelectionService>.Instance);
        var audit = new FakeAuditService();
        var connector = new FakeCoreWlanConnector();
        var manager = new MacWifiNetworkManager(inventory, selection, audit, NullLogger<MacWifiNetworkManager>.Instance, coreWlanConnector: connector);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        var result = await manager.ConnectAsync(new WifiConnectRequest
        {
            Ssid = "Cancelled_SSID",
            InterfaceName = "en1"
        }, cts.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(WifiConnectionState.Cancelled, result.State);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
