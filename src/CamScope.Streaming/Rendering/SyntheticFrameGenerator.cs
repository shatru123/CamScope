using System.Reflection;

namespace CamScope.Streaming.Rendering;

public static class SyntheticFrameGenerator
{
    private const int HeaderSize = 54;
    private const int StandardWidth = 640;
    private const int StandardHeight = 360;
    private const int StandardImageSize = StandardWidth * StandardHeight * 3; // 24bpp (691,200 bytes)
    private const int StandardFileSize = HeaderSize + StandardImageSize; // 691,254 bytes

    private static readonly Dictionary<string, byte[]> CachedScenes = new(StringComparer.OrdinalIgnoreCase);

    static SyntheticFrameGenerator()
    {
        LoadAllScenes();
    }

    private static void LoadAllScenes()
    {
        var asm = typeof(SyntheticFrameGenerator).Assembly;
        var names = asm.GetManifestResourceNames();

        LoadScene(asm, names, "entrance", "cctv_front_entrance.bmp");
        LoadScene(asm, names, "dock", "cctv_loading_dock.bmp");
        LoadScene(asm, names, "server", "cctv_server_room.bmp");
        LoadScene(asm, names, "reception", "cctv_reception_lobby.bmp");
        LoadScene(asm, names, "parking", "cctv_parking_lot.bmp");
        LoadScene(asm, names, "thermal", "cctv_perimeter_fence_thermal.bmp");
    }

    private static void LoadScene(Assembly asm, string[] allResourceNames, string key, string filename)
    {
        try
        {
            var match = allResourceNames.FirstOrDefault(n => n.EndsWith(filename, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;

            using var stream = asm.GetManifestResourceStream(match);
            if (stream == null) return;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            CachedScenes[key] = ms.ToArray();
        }
        catch
        {
            // Silently fall back to procedural rendering if loading fails
        }
    }

    public static byte[] GenerateBmpFrame(
        string cameraName,
        string host,
        long frameNumber,
        int width = 640,
        int height = 360)
    {
        var lowerName = cameraName.ToLowerInvariant();
        string sceneKey = "entrance";

        if (lowerName.Contains("dock") || lowerName.Contains("loading") || lowerName.Contains("ptz"))
        {
            sceneKey = "dock";
        }
        else if (lowerName.Contains("server") || lowerName.Contains("room") || lowerName.Contains("datacenter"))
        {
            sceneKey = "server";
        }
        else if (lowerName.Contains("reception") || lowerName.Contains("lobby") || lowerName.Contains("panoramic"))
        {
            sceneKey = "reception";
        }
        else if (lowerName.Contains("parking") || lowerName.Contains("lot") || lowerName.Contains("bullet"))
        {
            sceneKey = "parking";
        }
        else if (lowerName.Contains("thermal") || lowerName.Contains("perimeter") || lowerName.Contains("fence"))
        {
            sceneKey = "thermal";
        }

        byte[] buffer;
        if (width == StandardWidth && height == StandardHeight && CachedScenes.TryGetValue(sceneKey, out var baseBytes) && baseBytes.Length == StandardFileSize)
        {
            buffer = new byte[StandardFileSize];
            Buffer.BlockCopy(baseBytes, 0, buffer, 0, StandardFileSize);
        }
        else
        {
            var rowSize = width * 3;
            var imageSize = rowSize * height;
            var fileSize = HeaderSize + imageSize;
            buffer = new byte[fileSize];

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
            WriteInt16(buffer, 26, 1); // Planes
            WriteInt16(buffer, 28, 24); // 24 bits per pixel (BGR)
            WriteInt32(buffer, 30, 0); // BI_RGB (uncompressed)
            WriteInt32(buffer, 34, imageSize);
            WriteInt32(buffer, 38, 2835); // Horizontal resolution
            WriteInt32(buffer, 42, 2835); // Vertical resolution
            WriteInt32(buffer, 46, 0);
            WriteInt32(buffer, 50, 0);

            RenderFallbackProceduralScene(buffer, width, height, sceneKey, frameNumber);
        }

        // 3. Render Dynamic AI Object Detection Bounding Boxes & Scene Tracking
        RenderSceneTrackingOverlay(buffer, width, height, sceneKey, frameNumber);

        // 4. Subtle CCTV Scanline Effect
        for (int y = 0; y < height; y += 4)
        {
            var rowOffset = HeaderSize + (y * width) * 3;
            for (int x = 0; x < width; x++)
            {
                var idx = rowOffset + x * 3;
                buffer[idx] = (byte)(buffer[idx] * 0.90);
                buffer[idx + 1] = (byte)(buffer[idx + 1] * 0.90);
                buffer[idx + 2] = (byte)(buffer[idx + 2] * 0.90);
            }
        }

        // 5. Common Professional CCTV Telemetry / On-Screen Display (OSD)

        // Top translucent OSD bar
        DrawTranslucentRect(buffer, width, height, 0, 0, width, 44, 15, 12, 10, 0.65);
        DrawHLine(buffer, width, height, 0, 44, width, 55, 45, 30);

        // Header overlay: Camera Name & Profile parameters
        DrawString(buffer, width, height, 16, 10, cameraName.ToUpperInvariant(), 230, 190, 40);
        DrawString(buffer, width, height, 16, 25, $"{host} | ONVIF PROFILE S | 1080P @ 25FPS", 190, 190, 190);

        // Live UTC Timestamp (with millisecond precision)
        var timeStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC");
        DrawString(buffer, width, height, width - (timeStr.Length * 8) - 16, 10, timeStr, 255, 255, 255);

        // Recording indicator: Pulsing red dot with "REC [DEMO]"
        bool recPulse = (frameNumber / 15) % 2 == 0;
        if (recPulse)
        {
            DrawFilledCircle(buffer, width, height, width - 96, 28, 4, 30, 30, 230);
            DrawString(buffer, width, height, width - 84, 24, "REC [DEMO]", 30, 30, 230);
        }
        else
        {
            DrawFilledCircle(buffer, width, height, width - 96, 28, 4, 30, 140, 30);
            DrawString(buffer, width, height, width - 84, 24, "LIVE [DEMO]", 30, 140, 30);
        }

        // Center crosshair / optical reticle
        int cx = width / 2;
        int cy = height / 2;
        DrawHLine(buffer, width, height, cx - 14, cy, 28, 120, 120, 120);
        DrawVLine(buffer, width, height, cx, cy - 14, 28, 120, 120, 120);

        // Bottom translucent OSD bar
        DrawTranslucentRect(buffer, width, height, 0, height - 30, width, 30, 15, 12, 10, 0.65);
        DrawHLine(buffer, width, height, 0, height - 30, width, 55, 45, 30);

        // Bottom status string
        var statusStr = $"FRAME: {frameNumber:D6} | BITRATE: ~4120 KBPS | LOSS: 0.0%";
        DrawString(buffer, width, height, 16, height - 20, statusStr, 175, 175, 175);

        // Bottom-right watermark
        DrawString(buffer, width, height, width - 210, height - 20, "CAMSCOPE SURVEILLANCE", 150, 130, 80);

        return buffer;
    }

    #region Scene Tracking Overlay

    private static void RenderSceneTrackingOverlay(byte[] buf, int width, int height, string sceneKey, long frameNumber)
    {
        switch (sceneKey)
        {
            case "entrance":
                // Moving pedestrian tracking box
                double tEnt = (frameNumber % 160) / 160.0;
                int walkX = (int)(130 + Math.Sin(tEnt * Math.PI * 2) * 110);
                int walkY = 165;
                DrawRect(buf, width, height, walkX, walkY, 46, 76, 20, 220, 80);
                DrawString(buf, width, height, walkX, walkY - 12, "PERSON [98%]", 20, 220, 80);
                break;

            case "dock":
                // Moving forklift / logistics tracking box
                double tDock = (frameNumber % 180) / 180.0;
                int dockX = (int)(230 + Math.Sin(tDock * Math.PI * 2) * 90);
                int dockY = 190;
                DrawRect(buf, width, height, dockX, dockY, 84, 56, 20, 180, 245);
                DrawString(buf, width, height, dockX, dockY - 12, "FORKLIFT [95%]", 20, 180, 245);
                break;

            case "server":
                // Datacenter security perimeter & telemetry
                int aisleX = 210;
                int aisleY = 110;
                DrawRect(buf, width, height, aisleX, aisleY, 220, 85, 230, 160, 30);
                DrawString(buf, width, height, aisleX + 12, aisleY + 16, "ZONE SECURE: NO MOTION", 230, 160, 30);
                DrawString(buf, width, height, aisleX + 12, aisleY + 36, "TEMP: 19.4 C | HUMID: 42%", 180, 180, 180);
                DrawString(buf, width, height, aisleX + 12, aisleY + 56, "AIRFLOW: OPTIMAL (CRAC-1)", 40, 210, 50);

                // Blinking rack status LEDs
                for (int ry = 70; ry < 220; ry += 16)
                {
                    bool led1 = (frameNumber + ry) % 7 != 0;
                    bool led2 = (frameNumber / 2 + ry) % 5 == 0;
                    if (led1) SetPixel(buf, width, height, 130, ry, 30, 230, 40);
                    if (led2) SetPixel(buf, width, height, 138, ry, 230, 160, 20);

                    bool rled1 = (frameNumber + ry * 2) % 6 != 0;
                    bool rled2 = (frameNumber / 3 + ry) % 4 == 0;
                    if (rled1) SetPixel(buf, width, height, width - 138, ry, 30, 230, 40);
                    if (rled2) SetPixel(buf, width, height, width - 130, ry, 20, 180, 240);
                }
                break;

            case "reception":
                // Lobby visitor tracking box
                double tRec = (frameNumber % 150) / 150.0;
                int recX = (int)(230 + Math.Sin(tRec * Math.PI * 2) * 75);
                int recY = 160;
                DrawRect(buf, width, height, recX, recY, 50, 72, 230, 180, 30);
                DrawString(buf, width, height, recX, recY - 12, "VISITOR [96%]", 230, 180, 30);
                break;

            case "parking":
                // Moving car tracking box
                int carX = (int)(40 + ((frameNumber * 4) % 460));
                int carY = 210;
                DrawRect(buf, width, height, carX, carY, 95, 54, 20, 150, 245);
                DrawString(buf, width, height, carX, carY - 12, "CAR [94%] 22MPH", 20, 150, 245);
                break;

            case "thermal":
                // Thermal signature intruder tracking box
                double tTherm = (frameNumber % 140) / 140.0;
                int thermX = (int)(180 + Math.Sin(tTherm * Math.PI * 2) * 80);
                int thermY = 200;
                DrawRect(buf, width, height, thermX, thermY, 36, 68, 255, 255, 255);
                DrawString(buf, width, height, thermX, thermY - 12, "HEAT SIG [99%]", 255, 255, 255);
                break;
        }
    }

    private static void RenderFallbackProceduralScene(byte[] buf, int width, int height, string sceneKey, long frameNumber)
    {
        // Dark tactical grid background
        DrawFilledRect(buf, width, height, 0, 0, width, height, 25, 15, 11);
        for (int x = 40; x < width; x += 40)
        {
            DrawVLine(buf, width, height, x, 0, height, 59, 41, 30);
        }
        for (int y = 40; y < height; y += 40)
        {
            DrawHLine(buf, width, height, 0, y, width, 59, 41, 30);
        }
    }

    #endregion

    #region Pixel Drawing Helpers (24bpp BGR)

    public static void DrawTranslucentRect(byte[] buf, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r, double alpha = 0.65)
    {
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(width, x + w);
        int y0 = Math.Max(0, y);
        int y1 = Math.Min(height, y + h);

        double invAlpha = 1.0 - alpha;
        for (int py = y0; py < y1; py++)
        {
            int rowOffset = HeaderSize + (py * width) * 3;
            for (int px = x0; px < x1; px++)
            {
                int idx = rowOffset + px * 3;
                buf[idx] = (byte)(buf[idx] * invAlpha + b * alpha);
                buf[idx + 1] = (byte)(buf[idx + 1] * invAlpha + g * alpha);
                buf[idx + 2] = (byte)(buf[idx + 2] * invAlpha + r * alpha);
            }
        }
    }

    public static void DrawFilledRect(byte[] buf, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(width, x + w);
        int y0 = Math.Max(0, y);
        int y1 = Math.Min(height, y + h);

        for (int py = y0; py < y1; py++)
        {
            int rowOffset = HeaderSize + (py * width) * 3;
            for (int px = x0; px < x1; px++)
            {
                int idx = rowOffset + px * 3;
                buf[idx] = b;
                buf[idx + 1] = g;
                buf[idx + 2] = r;
            }
        }
    }

    public static void DrawHLine(byte[] buf, int width, int height, int x, int y, int length, byte b, byte g, byte r)
    {
        if (y < 0 || y >= height) return;
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(width, x + length);
        int rowOffset = HeaderSize + (y * width) * 3;
        for (int px = x0; px < x1; px++)
        {
            int idx = rowOffset + px * 3;
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
        }
    }

    public static void DrawVLine(byte[] buf, int width, int height, int x, int y, int length, byte b, byte g, byte r)
    {
        if (x < 0 || x >= width) return;
        int y0 = Math.Max(0, y);
        int y1 = Math.Min(height, y + length);
        for (int py = y0; py < y1; py++)
        {
            int idx = HeaderSize + (py * width + x) * 3;
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
        }
    }

    public static void SetPixel(byte[] buf, int width, int height, int x, int y, byte b, byte g, byte r)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var idx = HeaderSize + (y * width + x) * 3;
        buf[idx] = b;
        buf[idx + 1] = g;
        buf[idx + 2] = r;
    }

    public static void DrawRect(byte[] buf, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
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

    public static void DrawFilledCircle(byte[] buf, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
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

    public static void DrawString(byte[] buf, int width, int height, int x, int y, string text, byte b, byte g, byte r)
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

    #endregion
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
        ['/'] = [0x02, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x40, 0x00],
        ['('] = [0x0C, 0x18, 0x30, 0x30, 0x30, 0x18, 0x0C, 0x00],
        [')'] = [0x30, 0x18, 0x0C, 0x0C, 0x0C, 0x18, 0x30, 0x00],
        [','] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x30],
        ['!'] = [0x18, 0x18, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00],
        ['='] = [0x00, 0x00, 0x7E, 0x00, 0x7E, 0x00, 0x00, 0x00],
        ['@'] = [0x3C, 0x42, 0x99, 0xA5, 0x9D, 0x40, 0x3C, 0x00],
        ['*'] = [0x00, 0x24, 0x18, 0x7E, 0x18, 0x24, 0x00, 0x00],
        ['+'] = [0x00, 0x18, 0x18, 0x7E, 0x18, 0x18, 0x00, 0x00],
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
