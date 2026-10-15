namespace CamScope.Streaming.Rendering;

public static class SyntheticFrameGenerator
{
    private const int HeaderSize = 54;

    public static byte[] GenerateBmpFrame(
        string cameraName,
        string host,
        long frameNumber,
        int width = 640,
        int height = 360)
    {
        var rowSize = width * 4;
        var imageSize = rowSize * height;
        var fileSize = HeaderSize + imageSize;

        var buffer = new byte[fileSize];

        // 1. BMP Header
        buffer[0] = 0x42; // 'B'
        buffer[1] = 0x4D; // 'M'
        WriteInt32(buffer, 2, fileSize);
        WriteInt32(buffer, 6, 0); // Reserved
        WriteInt32(buffer, 10, HeaderSize); // Offset to pixels

        // 2. DIB Header (BITMAPINFOHEADER)
        WriteInt32(buffer, 14, 40); // Header size
        WriteInt32(buffer, 18, width);
        WriteInt32(buffer, 22, -height); // Top-down
        WriteInt16(buffer, 26, 1); // Color planes
        WriteInt16(buffer, 28, 32); // 32 bits per pixel (BGRA)
        WriteInt32(buffer, 30, 0); // BI_RGB (uncompressed)
        WriteInt32(buffer, 34, imageSize);
        WriteInt32(buffer, 38, 2835); // Horizontal resolution
        WriteInt32(buffer, 42, 2835); // Vertical resolution
        WriteInt32(buffer, 46, 0); // Colors in palette
        WriteInt32(buffer, 50, 0); // Important colors

        // 3. Pixel Drawing
        // Background: Slate Navy (#0B0F19 -> B:25, G:15, R:11, A:255)
        byte bgB = 25, bgG = 15, bgR = 11, bgA = 255;
        for (int i = HeaderSize; i < fileSize; i += 4)
        {
            buffer[i] = bgB;
            buffer[i + 1] = bgG;
            buffer[i + 2] = bgR;
            buffer[i + 3] = bgA;
        }

        // Draw grid lines (#1E293B -> B:59, G:41, R:30)
        byte gridB = 59, gridG = 41, gridR = 30;
        for (int x = 40; x < width; x += 40)
        {
            for (int y = 0; y < height; y++)
            {
                SetPixel(buffer, width, height, x, y, gridB, gridG, gridR);
            }
        }
        for (int y = 40; y < height; y += 40)
        {
            for (int x = 0; x < width; x++)
            {
                SetPixel(buffer, width, height, x, y, gridB, gridG, gridR);
            }
        }

        // Draw center crosshair (#06B6D4 -> B:212, G:182, R:6)
        int cx = width / 2;
        int cy = height / 2;
        for (int d = -16; d <= 16; d++)
        {
            SetPixel(buffer, width, height, cx + d, cy, 212, 182, 6);
            SetPixel(buffer, width, height, cx, cy + d, 212, 182, 6);
        }

        // Draw simulated motion target box (#F59E0B -> B:11, G:158, R:245)
        var t = (frameNumber % 120) / 120.0;
        var boxX = (int)(cx - 90 + Math.Sin(t * Math.PI * 2) * 80);
        var boxY = (int)(cy - 50 + Math.Cos(t * Math.PI * 2) * 40);
        DrawRect(buffer, width, height, boxX, boxY, 60, 60, 11, 158, 245);

        // Header overlay: Camera Name
        DrawString(buffer, width, height, 16, 14, cameraName.ToUpperInvariant(), 212, 182, 6);
        DrawString(buffer, width, height, 16, 28, $"{host} | H.264 1080P | 25 FPS", 180, 180, 180);

        // UTC Timestamp top-right
        var timeStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
        DrawString(buffer, width, height, width - (timeStr.Length * 8) - 16, 14, timeStr, 255, 255, 255);

        // Live badge
        DrawFilledCircle(buffer, width, height, width - 64, 32, 4, 129, 185, 16);
        DrawString(buffer, width, height, width - 52, 28, "LIVE", 129, 185, 16);

        // Footer status
        var statusStr = $"FRAME: {frameNumber:D6} | BITRATE: ~4120 KBPS | LOSS: 0.0%";
        DrawString(buffer, width, height, 16, height - 20, statusStr, 150, 150, 150);

        return buffer;
    }

    private static void SetPixel(byte[] buf, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var idx = HeaderSize + (y * width + x) * 4;
        buf[idx] = b;
        buf[idx + 1] = g;
        buf[idx + 2] = r;
        buf[idx + 3] = 255;
    }

    private static void DrawRect(byte[] buf, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        for (int i = 0; i < w; i++)
        {
            SetPixel(buf, width, height, x + i, y, b, g, r);
            SetPixel(buf, width, height, x + i, y + h - 1, b, g, r);
        }
        for (int j = 0; j < h; j++)
        {
            SetPixel(buf, width, height, x, y + j, b, g, r);
            SetPixel(buf, width, height, x + w - 1, y + j, b, g, r);
        }
    }

    private static void DrawFilledCircle(byte[] buf, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
    {
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius * radius)
                {
                    SetPixel(buf, width, height, cx + x, cy + y, b, g, r);
                }
            }
        }
    }

    private static void DrawString(byte[] buf, int width, int height, int x, int y, string text, byte b, byte g, byte r)
    {
        int curX = x;
        foreach (var ch in text)
        {
            DrawChar(buf, width, height, curX, y, ch, b, g, r);
            curX += 8;
        }
    }

    private static void DrawChar(byte[] buf, int width, int height, int startX, int startY, char c, byte b, byte g, byte r)
    {
        var glyph = SimpleFont.GetGlyph(c);
        for (int row = 0; row < 8; row++)
        {
            var bits = glyph[row];
            for (int col = 0; col < 8; col++)
            {
                if ((bits & (1 << (7 - col))) != 0)
                {
                    SetPixel(buf, width, height, startX + col, startY + row, b, g, r);
                }
            }
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}

internal static class SimpleFont
{
    private static readonly Dictionary<char, byte[]> FontData = new()
    {
        [' '] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [':'] = [0x00, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00],
        ['-'] = [0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00],
        ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00],
        ['|'] = [0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18],
        ['%'] = [0x62, 0x64, 0x08, 0x10, 0x26, 0x46, 0x00, 0x00],
        ['~'] = [0x00, 0x32, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00],
        ['#'] = [0x24, 0x24, 0x7E, 0x24, 0x7E, 0x24, 0x24, 0x00],
        ['['] = [0x3C, 0x30, 0x30, 0x30, 0x30, 0x30, 0x3C, 0x00],
        [']'] = [0x3C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x3C, 0x00],
        ['0'] = [0x3C, 0x66, 0x6E, 0x76, 0x66, 0x66, 0x3C, 0x00],
        ['1'] = [0x18, 0x38, 0x18, 0x18, 0x18, 0x18, 0x7E, 0x00],
        ['2'] = [0x3C, 0x66, 0x06, 0x1C, 0x30, 0x60, 0x7E, 0x00],
        ['3'] = [0x3C, 0x66, 0x06, 0x1C, 0x06, 0x66, 0x3C, 0x00],
        ['4'] = [0x0C, 0x1C, 0x34, 0x64, 0x7E, 0x04, 0x04, 0x00],
        ['5'] = [0x7E, 0x60, 0x7C, 0x06, 0x06, 0x66, 0x3C, 0x00],
        ['6'] = [0x1C, 0x30, 0x60, 0x7C, 0x66, 0x66, 0x3C, 0x00],
        ['7'] = [0x7E, 0x06, 0x0C, 0x18, 0x30, 0x30, 0x30, 0x00],
        ['8'] = [0x3C, 0x66, 0x66, 0x3C, 0x66, 0x66, 0x3C, 0x00],
        ['9'] = [0x3C, 0x66, 0x66, 0x3E, 0x06, 0x0C, 0x38, 0x00],
        ['A'] = [0x18, 0x3C, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00],
        ['B'] = [0x7C, 0x66, 0x66, 0x7C, 0x66, 0x66, 0x7C, 0x00],
        ['C'] = [0x3C, 0x66, 0x60, 0x60, 0x60, 0x66, 0x3C, 0x00],
        ['D'] = [0x78, 0x6C, 0x66, 0x66, 0x66, 0x6C, 0x78, 0x00],
        ['E'] = [0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x7E, 0x00],
        ['F'] = [0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x60, 0x00],
        ['G'] = [0x3C, 0x66, 0x60, 0x6E, 0x66, 0x66, 0x3A, 0x00],
        ['H'] = [0x66, 0x66, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00],
        ['I'] = [0x3C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00],
        ['J'] = [0x0E, 0x06, 0x06, 0x06, 0x06, 0x66, 0x3C, 0x00],
        ['K'] = [0x66, 0x6C, 0x78, 0x70, 0x78, 0x6C, 0x66, 0x00],
        ['L'] = [0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x7E, 0x00],
        ['M'] = [0x63, 0x77, 0x7F, 0x6B, 0x63, 0x63, 0x63, 0x00],
        ['N'] = [0x66, 0x76, 0x7E, 0x7E, 0x6E, 0x66, 0x66, 0x00],
        ['O'] = [0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00],
        ['P'] = [0x7C, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x00],
        ['Q'] = [0x3C, 0x66, 0x66, 0x66, 0x6A, 0x6C, 0x36, 0x00],
        ['R'] = [0x7C, 0x66, 0x66, 0x7C, 0x78, 0x6C, 0x66, 0x00],
        ['S'] = [0x3C, 0x66, 0x60, 0x3C, 0x06, 0x66, 0x3C, 0x00],
        ['T'] = [0x7E, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00],
        ['U'] = [0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00],
        ['V'] = [0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x18, 0x00],
        ['W'] = [0x63, 0x63, 0x63, 0x6B, 0x7F, 0x77, 0x63, 0x00],
        ['X'] = [0x66, 0x66, 0x3C, 0x18, 0x3C, 0x66, 0x66, 0x00],
        ['Y'] = [0x66, 0x66, 0x66, 0x3C, 0x18, 0x18, 0x18, 0x00],
        ['Z'] = [0x7E, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x7E, 0x00]
    };

    public static byte[] GetGlyph(char c)
    {
        var upper = char.ToUpperInvariant(c);
        if (FontData.TryGetValue(upper, out var glyph))
            return glyph;
        return FontData[' '];
    }
}
