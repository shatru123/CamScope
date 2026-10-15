using System.Xml;
using System.Xml.Linq;

namespace CamScope.Discovery.Parsing;

public static class SafeXmlParser
{
    private static readonly XmlReaderSettings SecureSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 1048576, // 1MB maximum to prevent XML bombs
        IgnoreComments = true,
        IgnoreWhitespace = true
    };

    public static XDocument ParseSafe(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, SecureSettings);
        return XDocument.Load(xmlReader);
    }

    public static XDocument ParseSafe(byte[] bytes, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (count <= 0 || count > bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        using var stream = new MemoryStream(bytes, 0, count);
        using var xmlReader = XmlReader.Create(stream, SecureSettings);
        return XDocument.Load(xmlReader);
    }
}
