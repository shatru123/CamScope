using System.Text;
using System.Xml;
using System.Xml.Linq;
using CamScope.Core.Enums;
using CamScope.Core.Interfaces;
using CamScope.Core.Models;
using Microsoft.Extensions.Logging;

namespace CamScope.CameraProtocols.Onvif;

public sealed class OnvifDeviceClient : IOnvifClient
{
    private static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1048576,
        IgnoreWhitespace = true,
        IgnoreComments = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<OnvifDeviceClient> _logger;

    public OnvifDeviceClient(HttpClient httpClient, ILogger<OnvifDeviceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public static void ValidateEndpoint(string serviceAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAddress);

        if (!Uri.TryCreate(serviceAddress, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid service URI: {serviceAddress}", nameof(serviceAddress));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Unsupported URI scheme '{uri.Scheme}'. Only HTTP and HTTPS are permitted.", nameof(serviceAddress));
        }
    }

    public async Task<bool> ProbeReachableAsync(string serviceAddress, TimeSpan timeout, CancellationToken ct = default)
    {
        ValidateEndpoint(serviceAddress);

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Get, serviceAddress);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe reachable failed for {Address}", serviceAddress);
            return false;
        }
    }

    public async Task<DeviceInformationResult> GetDeviceInformationAsync(
        string serviceAddress,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        ValidateEndpoint(serviceAddress);

        var soap = BuildSoapEnvelope(
            OnvifSecurity.CreateSecurityHeader(username, password),
            @"<tds:GetDeviceInformation xmlns:tds=""http://www.onvif.org/ver10/device/wsdl""/>");

        var responseXml = await SendSoapRequestAsync(serviceAddress, soap, ct);
        var doc = ParseSafe(responseXml);

        var manufacturer = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Manufacturer")?.Value.Trim() ?? "Unknown";
        var model = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Model")?.Value.Trim() ?? "ONVIF Camera";
        var firmware = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FirmwareVersion")?.Value.Trim() ?? "";
        var serial = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SerialNumber")?.Value.Trim() ?? "";
        var hardwareId = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "HardwareId")?.Value.Trim() ?? "";

        return new DeviceInformationResult(manufacturer, model, firmware, serial, hardwareId);
    }

    public async Task<DeviceCapabilities> GetCapabilitiesAsync(
        string serviceAddress,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        ValidateEndpoint(serviceAddress);

        try
        {
            var soap = BuildSoapEnvelope(
                OnvifSecurity.CreateSecurityHeader(username, password),
                @"<tds:GetCapabilities xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
                    <tds:Category>All</tds:Category>
                  </tds:GetCapabilities>");

            var responseXml = await SendSoapRequestAsync(serviceAddress, soap, ct);
            var doc = ParseSafe(responseXml);

            var hasMedia = doc.Descendants().Any(e => e.Name.LocalName == "Media" || e.Name.LocalName == "Media2");
            var hasPtz = doc.Descendants().Any(e => e.Name.LocalName == "PTZ");
            var hasSearch = doc.Descendants().Any(e => e.Name.LocalName == "Search" || e.Name.LocalName == "Recording");
            var hasEvents = doc.Descendants().Any(e => e.Name.LocalName == "Events");

            return new DeviceCapabilities
            {
                LiveStreaming = hasMedia ? CapabilityStatus.Supported : CapabilityStatus.Unsupported,
                Ptz = hasPtz ? CapabilityStatus.Supported : CapabilityStatus.Unsupported,
                RecordingSearch = hasSearch ? CapabilityStatus.Supported : CapabilityStatus.Unsupported,
                Analytics = hasEvents ? CapabilityStatus.Supported : CapabilityStatus.Unsupported
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve capabilities for {Address}", serviceAddress);
            return new DeviceCapabilities
            {
                LiveStreaming = CapabilityStatus.Supported // Default to supported for fallback
            };
        }
    }

    public async Task<string?> GetStreamUriAsync(
        string serviceAddress,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        ValidateEndpoint(serviceAddress);

        try
        {
            // First get profiles to obtain a ProfileToken
            var soapProfiles = BuildSoapEnvelope(
                OnvifSecurity.CreateSecurityHeader(username, password),
                @"<trt:GetProfiles xmlns:trt=""http://www.onvif.org/ver10/media/wsdl""/>");

            var responseXml = await SendSoapRequestAsync(serviceAddress, soapProfiles, ct);
            var doc = ParseSafe(responseXml);

            var profileToken = doc.Descendants()
                .Where(e => e.Name.LocalName == "Profiles")
                .Select(e => (string?)e.Attribute("token"))
                .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "Profile_1";

            // Next get stream URI for profile
            var soapStream = BuildSoapEnvelope(
                OnvifSecurity.CreateSecurityHeader(username, password),
                $@"<trt:GetStreamUri xmlns:trt=""http://www.onvif.org/ver10/media/wsdl"">
                     <trt:StreamSetup>
                       <tt:Stream xmlns:tt=""http://www.onvif.org/ver10/schema"">RTP-Unicast</tt:Stream>
                       <tt:Transport xmlns:tt=""http://www.onvif.org/ver10/schema"">
                         <tt:Protocol>RTSP</tt:Protocol>
                       </tt:Transport>
                     </trt:StreamSetup>
                     <trt:ProfileToken>{profileToken}</trt:ProfileToken>
                   </trt:GetStreamUri>");

            var streamResponseXml = await SendSoapRequestAsync(serviceAddress, soapStream, ct);
            var streamDoc = ParseSafe(streamResponseXml);

            var uriElement = streamDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri");
            return uriElement?.Value.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get RTSP stream URI for {Address}", serviceAddress);
            return null;
        }
    }

    public async Task<IReadOnlyList<RecordingSegment>> FindRecordingsAsync(
        string serviceAddress,
        DateTime startUtc,
        DateTime endUtc,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        ValidateEndpoint(serviceAddress);

        try
        {
            var soap = BuildSoapEnvelope(
                OnvifSecurity.CreateSecurityHeader(username, password),
                $@"<tse:FindRecordings xmlns:tse=""http://www.onvif.org/ver10/search/wsdl"">
                     <tse:Scope>
                       <tse:IncludedSources/>
                     </tse:Scope>
                   </tse:FindRecordings>");

            var responseXml = await SendSoapRequestAsync(serviceAddress, soap, ct);
            var doc = ParseSafe(responseXml);

            var searchToken = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SearchToken")?.Value;
            if (string.IsNullOrEmpty(searchToken))
            {
                return [];
            }

            // In actual ONVIF search service, GetRecordingSearchResults is called next
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Recording search not supported or returned error for {Address}: {Message}",
                serviceAddress, ex.Message);
            return [];
        }
    }

    private async Task<string> SendSoapRequestAsync(string serviceAddress, string soapXml, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, serviceAddress)
        {
            Content = new StringContent(soapXml, Encoding.UTF8, "application/soap+xml")
        };

        using var response = await _httpClient.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ONVIF service returned status {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractFault(body)}");
        }

        return body;
    }

    private static string BuildSoapEnvelope(string securityHeader, string bodyContent)
    {
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope"">
  <s:Header>{securityHeader}
  </s:Header>
  <s:Body>
    {bodyContent}
  </s:Body>
</s:Envelope>";
    }

    private static XDocument ParseSafe(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, SafeReaderSettings);
        return XDocument.Load(xmlReader);
    }

    private static string ExtractFault(string xml)
    {
        try
        {
            var doc = ParseSafe(xml);
            var reason = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text" || e.Name.LocalName == "Reason")?.Value;
            if (!string.IsNullOrWhiteSpace(reason)) return reason;
        }
        catch
        {
            // Ignore parse errors on fault extraction
        }
        return "Unknown SOAP Fault";
    }
}
