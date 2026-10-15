using System.Xml;
using CamScope.Core.Enums;
using CamScope.Discovery.Parsing;
using Xunit;

namespace CamScope.UnitTests.Parsing;

public sealed class WsDiscoveryParserTests
{
    private const string SampleProbeMatchSoap = @"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
  <s:Header>
    <a:MessageID>urn:uuid:12345678-1234-1234-1234-123456789abc</a:MessageID>
    <a:RelatesTo>urn:uuid:probe-request-id</a:RelatesTo>
    <a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>
  </s:Header>
  <s:Body>
    <d:ProbeMatches>
      <d:ProbeMatch>
        <a:EndpointReference>
          <a:Address>urn:uuid:98765432-4321-4321-4321-ba9876543210</a:Address>
        </a:EndpointReference>
        <d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types>
        <d:Scopes>
          onvif://www.onvif.org/type/NetworkVideoTransmitter
          onvif://www.onvif.org/name/North_Gate_Camera
          onvif://www.onvif.org/hardware/P3245-V
          onvif://www.onvif.org/location/Gate_North
          onvif://www.onvif.org/manufacturer/Axis_Communications
          onvif://www.onvif.org/Profile/Streaming
        </d:Scopes>
        <d:XAddrs>http://192.168.1.150:80/onvif/device_service http://10.0.0.150:8080/onvif/device_service</d:XAddrs>
        <d:MetadataVersion>1</d:MetadataVersion>
      </d:ProbeMatch>
    </d:ProbeMatches>
  </s:Body>
</s:Envelope>";

    [Fact]
    public void ParseProbeMatches_ValidSoapEnvelope_ExtractsCameraDeviceSuccessfully()
    {
        var devices = ProbeMatchParser.ParseProbeMatches(SampleProbeMatchSoap);

        Assert.Single(devices);
        var dev = devices[0];

        Assert.Equal("urn:uuid:98765432-4321-4321-4321-ba9876543210", dev.EndpointId);
        Assert.Equal("North Gate Camera", dev.DisplayName);
        Assert.Equal("Axis Communications", dev.Vendor);
        Assert.Equal("P3245-V", dev.Model);
        Assert.Equal("Gate North", dev.Location);
        Assert.Equal("192.168.1.150", dev.Host);
        Assert.Equal(80, dev.Port);
        Assert.Equal("http://192.168.1.150:80/onvif/device_service", dev.PrimaryServiceAddress);
        Assert.Equal(2, dev.ServiceAddresses.Count);
        Assert.Equal(CapabilityStatus.Supported, dev.Capabilities.LiveStreaming);
    }

    [Fact]
    public void ParseScopesMetadata_UrlEncodedScopes_DecodesProperly()
    {
        var scopes = new[]
        {
            "onvif://www.onvif.org/name/Front%20Door%20Camera",
            "onvif://www.onvif.org/hardware/Model_XYZ",
            "onvif://www.onvif.org/location/Floor%202"
        };

        var (name, hardware, location, vendor) = ProbeMatchParser.ParseScopesMetadata(scopes);

        Assert.Equal("Front Door Camera", name);
        Assert.Equal("Model XYZ", hardware);
        Assert.Equal("Floor 2", location);
        Assert.Null(vendor);
    }
}

public sealed class XmlSecurityTests
{
    [Fact]
    public void SafeXmlParser_PayloadWithExternalDtd_ThrowsXmlException()
    {
        const string xxePayload = @"<?xml version=""1.0"" encoding=""utf-8""?>
<!DOCTYPE test [
  <!ENTITY xxe SYSTEM ""file:///etc/passwd"">
]>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Body>
    <data>&xxe;</data>
  </s:Body>
</s:Envelope>";

        Assert.Throws<XmlException>(() => SafeXmlParser.ParseSafe(xxePayload));
    }

    [Fact]
    public void SafeXmlParser_EmptyXml_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SafeXmlParser.ParseSafe(""));
    }
}
