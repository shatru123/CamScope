using CamScope.Application.Security;
using CamScope.CameraProtocols.Onvif;
using CamScope.Core.Enums;
using CamScope.Core.Models;
using CamScope.Discovery.Deduplication;
using CamScope.Streaming.Sessions;
using Xunit;

namespace CamScope.UnitTests.SecurityAndDeduplication;

public sealed class DeviceDeduplicatorTests
{
    [Fact]
    public void Deduplicator_SameEndpoint_MergesScopesAndEndpoints()
    {
        var dedup = new DeviceDeduplicator();

        var device1 = new CameraDevice
        {
            EndpointId = "urn:uuid:camera-100",
            DisplayName = "Camera 100",
            Vendor = "Unknown",
            Model = "ONVIF Compatible",
            Host = "192.168.1.100",
            Port = 80,
            ServiceAddresses = ["http://192.168.1.100/onvif/device_service"],
            Scopes = ["onvif://www.onvif.org/type/NetworkVideoTransmitter"]
        };

        var device2 = new CameraDevice
        {
            EndpointId = "urn:uuid:camera-100",
            DisplayName = "Camera 100",
            Vendor = "Axis",
            Model = "M3045-V",
            Host = "192.168.1.100",
            Port = 80,
            ServiceAddresses = ["http://192.168.1.100:8080/onvif/device_service"],
            Scopes = ["onvif://www.onvif.org/name/Office_Cam"]
        };

        var added1 = dedup.TryAddOrUpdate(device1, out var dev1);
        var added2 = dedup.TryAddOrUpdate(device2, out var dev2);

        Assert.True(added1);
        Assert.False(added2); // Not added as new; updated in-place

        Assert.Single(dedup.GetAll());
        Assert.Equal("Axis", dev2.Vendor);
        Assert.Equal("M3045-V", dev2.Model);
        Assert.Equal(2, dev2.ServiceAddresses.Count);
        Assert.Equal(2, dev2.Scopes.Count);
    }
}

public sealed class CredentialRedactionTests
{
    [Fact]
    public void SecretRedactor_PasswordInText_IsRedacted()
    {
        var raw = "Failed login for user admin with password: SuperSecretPassword123! at endpoint";
        var redacted = SecretRedactor.Redact(raw);

        Assert.DoesNotContain("SuperSecretPassword123!", redacted);
        Assert.Contains("password: ***REDACTED***", redacted);
    }

    [Fact]
    public void SecretRedactor_RtspUrlCredentials_AreRedacted()
    {
        var url = "rtsp://admin:mypassword999@192.168.1.50:554/live/ch0";
        var redacted = SecretRedactor.Redact(url);

        Assert.DoesNotContain("mypassword999", redacted);
        Assert.Equal("rtsp://admin:***REDACTED***@192.168.1.50:554/live/ch0", redacted);
    }

    [Fact]
    public void SecretRedactor_AuthorizationBearer_IsRedacted()
    {
        var raw = "Sending request with Authorization Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var redacted = SecretRedactor.Redact(raw);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted);
        Assert.Contains("Bearer ***REDACTED***", redacted);
    }
}

public sealed class ProtocolSecurityTests
{
    [Fact]
    public void OnvifSecurity_CreateUsernameTokenDigest_ProducesValidSha1Digest()
    {
        var (digest, nonce, created) = OnvifSecurity.CreateUsernameTokenDigest("adminPass");

        Assert.NotEmpty(digest);
        Assert.NotEmpty(nonce);
        Assert.NotEmpty(created);

        // Verify Nonce is valid Base64
        var nonceBytes = Convert.FromBase64String(nonce);
        Assert.Equal(16, nonceBytes.Length);

        // Verify Digest is valid Base64
        var digestBytes = Convert.FromBase64String(digest);
        Assert.Equal(20, digestBytes.Length); // SHA-1 is 20 bytes (160 bits)
    }

    [Fact]
    public void OnvifDeviceClient_ValidateEndpoint_RejectsInvalidSchemes()
    {
        Assert.Throws<ArgumentException>(() => OnvifDeviceClient.ValidateEndpoint("file:///etc/passwd"));
        Assert.Throws<ArgumentException>(() => OnvifDeviceClient.ValidateEndpoint("gopher://127.0.0.1"));
        Assert.Throws<ArgumentException>(() => OnvifDeviceClient.ValidateEndpoint("ftp://camera.local"));

        // Valid endpoints should not throw
        OnvifDeviceClient.ValidateEndpoint("http://192.168.1.100/onvif/device_service");
        OnvifDeviceClient.ValidateEndpoint("https://192.168.1.100:8443/onvif/device_service");
    }

    [Fact]
    public void BackoffCalculator_ExponentialGrowth_CapsAtMaxDelay()
    {
        Assert.Equal(1, RtspStreamSession.CalculateBackoffDelaySeconds(1, 30));
        Assert.Equal(2, RtspStreamSession.CalculateBackoffDelaySeconds(2, 30));
        Assert.Equal(4, RtspStreamSession.CalculateBackoffDelaySeconds(3, 30));
        Assert.Equal(8, RtspStreamSession.CalculateBackoffDelaySeconds(4, 30));
        Assert.Equal(16, RtspStreamSession.CalculateBackoffDelaySeconds(5, 30));
        Assert.Equal(30, RtspStreamSession.CalculateBackoffDelaySeconds(6, 30)); // Capped at 30
        Assert.Equal(30, RtspStreamSession.CalculateBackoffDelaySeconds(10, 30));
    }
}
