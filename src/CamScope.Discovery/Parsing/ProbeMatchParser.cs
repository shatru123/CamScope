using System.Net;
using System.Xml.Linq;
using CamScope.Core.Enums;
using CamScope.Core.Models;

namespace CamScope.Discovery.Parsing;

public static class ProbeMatchParser
{
    public static IReadOnlyList<CameraDevice> ParseProbeMatches(string xml)
    {
        var doc = SafeXmlParser.ParseSafe(xml);
        return ParseProbeMatches(doc);
    }

    public static IReadOnlyList<CameraDevice> ParseProbeMatches(XDocument doc)
    {
        var devices = new List<CameraDevice>();

        // Find all ProbeMatch elements across varying namespaces
        var probeMatches = doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch");

        foreach (var match in probeMatches)
        {
            var device = ParseSingleProbeMatch(match);
            if (device != null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    public static CameraDevice? ParseSingleProbeMatch(XElement match)
    {
        var endpointElement = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address");
        var endpointId = endpointElement?.Value.Trim() ?? string.Empty;

        var xaddrsElement = match.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs");
        var xaddrsRaw = xaddrsElement?.Value.Trim() ?? string.Empty;

        var scopesElement = match.Elements().FirstOrDefault(e => e.Name.LocalName == "Scopes");
        var scopesRaw = scopesElement?.Value.Trim() ?? string.Empty;

        var typesElement = match.Elements().FirstOrDefault(e => e.Name.LocalName == "Types");
        var typesRaw = typesElement?.Value.Trim() ?? string.Empty;

        var xaddrsList = xaddrsRaw
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(IsValidHttpEndpoint)
            .ToList();

        if (xaddrsList.Count == 0 && string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        var scopesList = scopesRaw
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var (name, hardware, location, vendor) = ParseScopesMetadata(scopesList);

        // Derive host & port from primary XAddr if available
        var primaryAddress = xaddrsList.FirstOrDefault();
        string host = "Unknown";
        int port = 80;

        if (primaryAddress != null && Uri.TryCreate(primaryAddress, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            port = uri.Port;
        }

        var displayName = !string.IsNullOrWhiteSpace(name) ? name : $"Camera @ {host}:{port}";

        return new CameraDevice
        {
            EndpointId = string.IsNullOrWhiteSpace(endpointId) ? $"endpoint:{host}:{port}" : endpointId,
            DisplayName = displayName,
            Vendor = !string.IsNullOrWhiteSpace(vendor) ? vendor : "Unknown",
            Model = !string.IsNullOrWhiteSpace(hardware) ? hardware : "ONVIF Compatible",
            Host = host,
            Port = port,
            PrimaryServiceAddress = primaryAddress,
            ServiceAddresses = xaddrsList,
            Scopes = scopesList,
            ConnectionState = DeviceConnectionState.Discovered,
            AuthState = AuthenticationState.Unauthenticated,
            Capabilities = new DeviceCapabilities
            {
                LiveStreaming = scopesRaw.Contains("Streaming", StringComparison.OrdinalIgnoreCase)
                    ? CapabilityStatus.Supported
                    : CapabilityStatus.Unknown,
                RecordingSearch = scopesRaw.Contains("Recording", StringComparison.OrdinalIgnoreCase)
                    ? CapabilityStatus.Supported
                    : CapabilityStatus.Unknown
            },
            Protocol = CameraProtocolType.Onvif,
            Location = location,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
    }

    public static (string? Name, string? Hardware, string? Location, string? Vendor) ParseScopesMetadata(IEnumerable<string> scopes)
    {
        string? name = null;
        string? hardware = null;
        string? location = null;
        string? vendor = null;

        foreach (var rawScope in scopes)
        {
            var scope = WebUtility.UrlDecode(rawScope);

            if (scope.Contains("/name/", StringComparison.OrdinalIgnoreCase))
            {
                name = ExtractScopeSuffix(scope, "/name/");
            }
            else if (scope.Contains("/hardware/", StringComparison.OrdinalIgnoreCase))
            {
                hardware = ExtractScopeSuffix(scope, "/hardware/");
            }
            else if (scope.Contains("/location/", StringComparison.OrdinalIgnoreCase))
            {
                location = ExtractScopeSuffix(scope, "/location/");
            }
            else if (scope.Contains("/mfr/", StringComparison.OrdinalIgnoreCase) ||
                     scope.Contains("/manufacturer/", StringComparison.OrdinalIgnoreCase))
            {
                vendor = ExtractScopeSuffix(scope, scope.Contains("/mfr/") ? "/mfr/" : "/manufacturer/");
            }
        }

        return (name, hardware, location, vendor);
    }

    private static string ExtractScopeSuffix(string scope, string marker)
    {
        var idx = scope.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var value = scope[(idx + marker.Length)..].Trim();
            return value.Replace('_', ' ');
        }
        return scope;
    }

    private static bool IsValidHttpEndpoint(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
        return false;
    }
}
