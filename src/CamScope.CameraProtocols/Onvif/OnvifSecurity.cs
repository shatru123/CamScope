using System.Security.Cryptography;
using System.Text;

namespace CamScope.CameraProtocols.Onvif;

public static class OnvifSecurity
{
    public static (string PasswordDigest, string NonceBase64, string CreatedUtc) CreateUsernameTokenDigest(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        // 16 cryptographically random bytes for Nonce
        var rawNonce = new byte[16];
        RandomNumberGenerator.Fill(rawNonce);
        var nonceBase64 = Convert.ToBase64String(rawNonce);

        // ISO-8601 UTC timestamp
        var createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var createdBytes = Encoding.UTF8.GetBytes(createdUtc);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        // Digest = Base64(SHA-1(rawNonce + createdBytes + passwordBytes))
        var combined = new byte[rawNonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(rawNonce, 0, combined, 0, rawNonce.Length);
        Buffer.BlockCopy(createdBytes, 0, combined, rawNonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, combined, rawNonce.Length + createdBytes.Length, passwordBytes.Length);

        var hash = SHA1.HashData(combined);
        var digest = Convert.ToBase64String(hash);

        return (digest, nonceBase64, createdUtc);
    }

    public static string CreateSecurityHeader(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return string.Empty;

        var (digest, nonce, created) = CreateUsernameTokenDigest(password);

        return $@"
    <wsse:Security xmlns:wsse=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd""
                   xmlns:wsu=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"">
      <wsse:UsernameToken>
        <wsse:Username>{EscapeXml(username)}</wsse:Username>
        <wsse:Password Type=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"">{digest}</wsse:Password>
        <wsse:Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonce}</wsse:Nonce>
        <wsu:Created>{created}</wsu:Created>
      </wsse:UsernameToken>
    </wsse:Security>";
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;");
}
