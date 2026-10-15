using System.Text.RegularExpressions;

namespace CamScope.Application.Security;

public static partial class SecretRedactor
{
    private static readonly Regex PasswordRegex = new(
        @"(?<prefix>(?:password|pwd|secret|token|apikey|authorization)\s*[:=]\s*[""']?)[^""'\s,;&]+(?<suffix>[""']?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BasicAuthRegex = new(
        @"Basic\s+[A-Za-z0-9+/=]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BearerAuthRegex = new(
        @"Bearer\s+[A-Za-z0-9._\-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RtspUrlPasswordRegex = new(
        @"rtsp://(?<user>[^:]+):(?<pass>[^@]+)@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var result = PasswordRegex.Replace(input, "${prefix}***REDACTED***${suffix}");
        result = BasicAuthRegex.Replace(result, "Basic ***REDACTED***");
        result = BearerAuthRegex.Replace(result, "Bearer ***REDACTED***");
        result = RtspUrlPasswordRegex.Replace(result, "rtsp://${user}:***REDACTED***@");

        return result;
    }
}
