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

        // 3. Render Camera-Specific Realistic Surveillance Scene
        var lowerName = cameraName.ToLowerInvariant();
        if (lowerName.Contains("entrance") || lowerName.Contains("dome") || lowerName.Contains("front"))
        {
            RenderFrontEntranceScene(buffer, width, height, frameNumber);
        }
        else if (lowerName.Contains("dock") || lowerName.Contains("loading") || lowerName.Contains("ptz"))
        {
            RenderLoadingDockScene(buffer, width, height, frameNumber);
        }
        else if (lowerName.Contains("server") || lowerName.Contains("room"))
        {
            RenderServerRoomScene(buffer, width, height, frameNumber);
        }
        else if (lowerName.Contains("reception") || lowerName.Contains("panoramic") || lowerName.Contains("lobby"))
        {
            RenderReceptionScene(buffer, width, height, frameNumber);
        }
        else if (lowerName.Contains("parking") || lowerName.Contains("lot") || lowerName.Contains("bullet"))
        {
            RenderParkingLotScene(buffer, width, height, frameNumber);
        }
        else
        {
            RenderDefaultSurveillanceScene(buffer, width, height, frameNumber);
        }

        // 4. Common Professional CCTV Telemetry / On-Screen Display (OSD)

        // Subtle camera scanline effect
        for (int y = 0; y < height; y += 4)
        {
            var rowOffset = HeaderSize + (y * width) * 4;
            for (int x = 0; x < width; x++)
            {
                var idx = rowOffset + x * 4;
                buffer[idx] = (byte)(buffer[idx] * 0.88);
                buffer[idx + 1] = (byte)(buffer[idx + 1] * 0.88);
                buffer[idx + 2] = (byte)(buffer[idx + 2] * 0.88);
            }
        }

        // Top translucent OSD bar
        DrawFilledRect(buffer, width, height, 0, 0, width, 44, 20, 15, 10);
        DrawHLine(buffer, width, height, 0, 44, width, 55, 45, 30);

        // Header overlay: Camera Name & Vendor / IP
        DrawString(buffer, width, height, 16, 10, cameraName.ToUpperInvariant(), 220, 180, 20);
        DrawString(buffer, width, height, 16, 24, $"{host} | ONVIF H.264 | 1080P @ 25FPS", 180, 180, 180);

        // UTC Timestamp top-right
        var timeStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
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
        DrawFilledRect(buffer, width, height, 0, height - 32, width, 32, 20, 15, 10);
        DrawHLine(buffer, width, height, 0, height - 32, width, 55, 45, 30);

        // Bottom status string
        var statusStr = $"FRAME: {frameNumber:D6} | BITRATE: ~4120 KBPS | LOSS: 0.0%";
        DrawString(buffer, width, height, 16, height - 20, statusStr, 170, 170, 170);

        // Bottom-right watermark
        DrawString(buffer, width, height, width - 210, height - 20, "CAMSCOPE SURVEILLANCE", 140, 120, 80);

        return buffer;
    }

    #region Scene Renderers

    private static void RenderFrontEntranceScene(byte[] buf, int width, int height, long frameNumber)
    {
        // 1. Sky & Exterior Building Facade
        DrawFilledRect(buf, width, height, 0, 0, width, 140, 60, 45, 35);
        DrawFilledRect(buf, width, height, 60, 20, width - 120, 50, 95, 75, 45);

        // 2. Concrete Plaza Flooring
        DrawFilledRect(buf, width, height, 0, 140, width, height - 140, 32, 28, 24);

        // Plaza pavement grid lines
        for (int py = 160; py < height - 32; py += 30)
        {
            DrawHLine(buf, width, height, 0, py, width, 48, 42, 36);
        }
        for (int px = 40; px < width; px += 70)
        {
            for (int py = 140; py < height - 32; py++)
            {
                SetPixel(buf, width, height, px + (py - 140) / 4, py, 48, 42, 36);
            }
        }

        // 3. Central Double Glass Entrance Doors
        int doorX = 220;
        int doorY = 70;
        int doorW = 200;
        int doorH = 150;
        DrawFilledRect(buf, width, height, doorX, doorY, doorW, doorH, 70, 55, 40);
        DrawRect(buf, width, height, doorX, doorY, doorW, doorH, 140, 130, 110);
        DrawVLine(buf, width, height, doorX + doorW / 2, doorY, doorH, 140, 130, 110);

        // Door handles
        DrawVLine(buf, width, height, doorX + doorW / 2 - 8, doorY + 60, 30, 200, 200, 200);
        DrawVLine(buf, width, height, doorX + doorW / 2 + 8, doorY + 60, 30, 200, 200, 200);

        // Entrance welcome mat
        DrawFilledRect(buf, width, height, doorX - 10, doorY + doorH, doorW + 20, 26, 18, 16, 14);

        // 4. Structural Columns Left & Right
        DrawFilledRect(buf, width, height, 90, 50, 40, 170, 75, 68, 60);
        DrawRect(buf, width, height, 90, 50, 40, 170, 95, 88, 80);
        DrawFilledRect(buf, width, height, 510, 50, 40, 170, 75, 68, 60);
        DrawRect(buf, width, height, 510, 50, 40, 170, 95, 88, 80);

        // 5. Motion Tracking Box
        var t = (frameNumber % 160) / 160.0;
        int walkX = (int)(110 + t * 400);
        int walkY = 170;
        DrawRect(buf, width, height, walkX, walkY, 44, 76, 20, 220, 80);
        DrawString(buf, width, height, walkX, walkY - 12, "PERSON [98%]", 20, 220, 80);
    }

    private static void RenderLoadingDockScene(byte[] buf, int width, int height, long frameNumber)
    {
        // 1. Industrial Concrete Warehouse Wall
        DrawFilledRect(buf, width, height, 0, 0, width, 180, 40, 32, 26);

        // 2. Loading Bay 03 and Bay 04 Roll-up Overhead Doors
        int bay1X = 60, bayW = 220, bayH = 150, bayY = 30;
        int bay2X = 360;

        // Bay 03
        DrawFilledRect(buf, width, height, bay1X, bayY, bayW, bayH, 60, 48, 38);
        for (int sy = bayY + 12; sy < bayY + bayH; sy += 14)
        {
            DrawHLine(buf, width, height, bay1X, sy, bayW, 35, 28, 22);
        }
        DrawRect(buf, width, height, bay1X, bayY, bayW, bayH, 120, 100, 80);
        DrawString(buf, width, height, bay1X + 80, bayY + 12, "BAY 03", 220, 220, 220);

        // Bay 04 (Partially open)
        DrawFilledRect(buf, width, height, bay2X, bayY, bayW, bayH - 40, 60, 48, 38);
        for (int sy = bayY + 12; sy < bayY + bayH - 40; sy += 14)
        {
            DrawHLine(buf, width, height, bay2X, sy, bayW, 35, 28, 22);
        }
        DrawFilledRect(buf, width, height, bay2X, bayY + bayH - 40, bayW, 40, 15, 12, 10);
        DrawRect(buf, width, height, bay2X, bayY, bayW, bayH, 120, 100, 80);
        DrawString(buf, width, height, bay2X + 80, bayY + 12, "BAY 04", 220, 220, 220);

        // 3. Platform & Dock Apron
        DrawFilledRect(buf, width, height, 0, 180, width, height - 180, 24, 20, 16);

        // High-visibility hazard diagonal stripes
        for (int x = 0; x < width; x += 24)
        {
            bool isYellow = (x / 24) % 2 == 0;
            byte b = (byte)(isYellow ? 20 : 15);
            byte g = (byte)(isYellow ? 180 : 15);
            byte r = (byte)(isYellow ? 245 : 15);
            DrawFilledRect(buf, width, height, x, 180, 24, 12, b, g, r);
        }

        // 4. Motion Target
        var t = (frameNumber % 140) / 140.0;
        int truckX = (int)(bay2X + 30 + Math.Sin(t * Math.PI * 2) * 40);
        int truckY = 200;
        DrawRect(buf, width, height, truckX, truckY, 90, 60, 20, 180, 245);
        DrawString(buf, width, height, truckX, truckY - 12, "VEHICLE [95%]", 20, 180, 245);
    }

    private static void RenderServerRoomScene(byte[] buf, int width, int height, long frameNumber)
    {
        // 1. Dark Data Center Background
        DrawFilledRect(buf, width, height, 0, 0, width, height, 13, 7, 5);

        // 2. Raised Floor Perforated Tiles
        DrawFilledRect(buf, width, height, 0, 220, width, height - 220, 24, 18, 14);
        for (int y = 220; y < height - 32; y += 28)
        {
            DrawHLine(buf, width, height, 0, y, width, 40, 32, 26);
        }
        for (int x = 30; x < width; x += 55)
        {
            DrawVLine(buf, width, height, x, 220, height - 252, 40, 32, 26);
        }

        // 3. 42U Server Racks on Left and Right Sides
        int rackW = 160;

        // Left Racks
        DrawFilledRect(buf, width, height, 0, 44, rackW, 180, 22, 16, 12);
        DrawRect(buf, width, height, 0, 44, rackW, 180, 60, 50, 40);

        // Right Racks
        DrawFilledRect(buf, width, height, width - rackW, 44, rackW, 180, 22, 16, 12);
        DrawRect(buf, width, height, width - rackW, 44, rackW, 180, 60, 50, 40);

        // Server Chassis Dividers & Status LEDs
        for (int ry = 52; ry < 220; ry += 12)
        {
            DrawHLine(buf, width, height, 6, ry, rackW - 12, 35, 28, 20);
            DrawHLine(buf, width, height, width - rackW + 6, ry, rackW - 12, 35, 28, 20);

            bool led1 = (frameNumber + ry) % 7 != 0;
            bool led2 = (frameNumber / 2 + ry) % 5 == 0;
            if (led1) SetPixel(buf, width, height, 14, ry + 4, 30, 230, 40);
            if (led2) SetPixel(buf, width, height, 22, ry + 4, 230, 160, 20);

            bool rled1 = (frameNumber + ry * 2) % 6 != 0;
            bool rled2 = (frameNumber / 3 + ry) % 4 == 0;
            if (rled1) SetPixel(buf, width, height, width - 24, ry + 4, 30, 230, 40);
            if (rled2) SetPixel(buf, width, height, width - 34, ry + 4, 20, 180, 240);
        }

        // 4. Overhead Blue Light
        DrawFilledRect(buf, width, height, rackW, 44, width - rackW * 2, 8, 240, 140, 20);

        // 5. Center Security Zone Box
        int aisleX = 220;
        int aisleY = 110;
        DrawRect(buf, width, height, aisleX, aisleY, 200, 95, 230, 160, 30);
        DrawString(buf, width, height, aisleX + 16, aisleY + 20, "ZONE SECURE: NO MOTION", 230, 160, 30);
        DrawString(buf, width, height, aisleX + 16, aisleY + 38, "TEMP: 19.4 C | HUMID: 42%", 180, 180, 180);
        DrawString(buf, width, height, aisleX + 16, aisleY + 54, "AIRFLOW: OPTIMAL", 40, 210, 50);
    }

    private static void RenderReceptionScene(byte[] buf, int width, int height, long frameNumber)
    {
        // 1. Architectural Wood Feature Wall
        DrawFilledRect(buf, width, height, 0, 0, width, 160, 28, 38, 54);
        for (int wx = 10; wx < width; wx += 14)
        {
            DrawVLine(buf, width, height, wx, 0, 160, 20, 28, 42);
        }

        // Corporate Plaque
        int logoX = 230;
        int logoY = 55;
        DrawFilledRect(buf, width, height, logoX, logoY, 180, 36, 40, 30, 20);
        DrawRect(buf, width, height, logoX, logoY, 180, 36, 240, 180, 50);
        DrawString(buf, width, height, logoX + 18, logoY + 14, "GLOBAL HEADQUARTERS", 240, 240, 240);

        // 2. Marble Lobby Flooring
        DrawFilledRect(buf, width, height, 0, 160, width, height - 160, 28, 24, 20);
        for (int my = 175; my < height - 32; my += 35)
        {
            DrawHLine(buf, width, height, 0, my, width, 42, 36, 30);
        }

        // 3. Reception Counter
        int deskX = 170;
        int deskY = 145;
        int deskW = 300;
        int deskH = 65;
        DrawFilledRect(buf, width, height, deskX, deskY, deskW, deskH, 70, 60, 50);
        DrawRect(buf, width, height, deskX, deskY, deskW, deskH, 120, 105, 90);
        DrawFilledRect(buf, width, height, deskX - 10, deskY, deskW + 20, 10, 160, 150, 140);
        DrawHLine(buf, width, height, deskX, deskY + deskH - 2, deskW, 20, 180, 245);

        // 4. Potted Plant
        DrawFilledCircle(buf, width, height, 60, 175, 22, 35, 90, 40);
        DrawFilledCircle(buf, width, height, 75, 165, 18, 30, 80, 35);
        DrawFilledRect(buf, width, height, 50, 185, 30, 35, 45, 40, 35);

        // 5. Tracking Box
        var t = (frameNumber % 180) / 180.0;
        int trackX = (int)(220 + Math.Sin(t * Math.PI * 2) * 60);
        int trackY = 175;
        DrawRect(buf, width, height, trackX, trackY, 50, 65, 230, 180, 30);
        DrawString(buf, width, height, trackX, trackY - 12, "LOBBY 91%", 230, 180, 30);
    }

    private static void RenderParkingLotScene(byte[] buf, int width, int height, long frameNumber)
    {
        // 1. Twilight Sky & Horizon
        DrawFilledRect(buf, width, height, 0, 0, width, 110, 45, 28, 18);
        DrawFilledRect(buf, width, height, 0, 110, width, 25, 25, 22, 18);
        DrawHLine(buf, width, height, 0, 134, width, 60, 50, 40);

        // 2. Asphalt Parking Lot
        DrawFilledRect(buf, width, height, 0, 135, width, height - 135, 22, 18, 15);

        // Painted stalls
        for (int px = 50; px < width - 60; px += 85)
        {
            for (int py = 160; py < height - 32; py++)
            {
                SetPixel(buf, width, height, px - (py - 160) / 3, py, 180, 180, 180);
            }
        }

        // Parked Cars
        DrawFilledRect(buf, width, height, 70, 175, 60, 35, 45, 38, 30);
        DrawFilledRect(buf, width, height, 240, 175, 60, 35, 35, 30, 55);

        // Floodlights
        DrawVLine(buf, width, height, 160, 60, 100, 90, 80, 70);
        DrawFilledCircle(buf, width, height, 160, 60, 6, 20, 220, 250);

        DrawVLine(buf, width, height, 480, 60, 100, 90, 80, 70);
        DrawFilledCircle(buf, width, height, 480, 60, 6, 20, 220, 250);

        // 3. Moving Vehicle Target Box
        var t = (frameNumber % 120) / 120.0;
        int carX = (int)(20 + t * 480);
        int carY = 225;
        DrawRect(buf, width, height, carX, carY, 80, 48, 20, 150, 245);
        DrawString(buf, width, height, carX, carY - 12, "CAR [94%] 22MPH", 20, 150, 245);
    }

    private static void RenderDefaultSurveillanceScene(byte[] buf, int width, int height, long frameNumber)
    {
        DrawFilledRect(buf, width, height, 0, 0, width, height, 25, 15, 11);
        for (int x = 40; x < width; x += 40)
        {
            DrawVLine(buf, width, height, x, 0, height, 59, 41, 30);
        }
        for (int y = 40; y < height; y += 40)
        {
            DrawHLine(buf, width, height, 0, y, width, 59, 41, 30);
        }

        int cx = width / 2;
        int cy = height / 2;
        var t = (frameNumber % 120) / 120.0;
        var boxX = (int)(cx - 90 + Math.Sin(t * Math.PI * 2) * 80);
        var boxY = (int)(cy - 50 + Math.Cos(t * Math.PI * 2) * 40);
        DrawRect(buf, width, height, boxX, boxY, 60, 60, 11, 158, 245);
        DrawString(buf, width, height, boxX, boxY - 12, "TARGET ACQUIRED", 11, 158, 245);
    }

    #endregion

    #region Pixel Drawing Helpers

    public static void DrawFilledRect(byte[] buf, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(width, x + w);
        int y0 = Math.Max(0, y);
        int y1 = Math.Min(height, y + h);

        for (int py = y0; py < y1; py++)
        {
            int rowOffset = HeaderSize + (py * width) * 4;
            for (int px = x0; px < x1; px++)
            {
                int idx = rowOffset + px * 4;
                buf[idx] = b;
                buf[idx + 1] = g;
                buf[idx + 2] = r;
                buf[idx + 3] = 255;
            }
        }
    }

    public static void DrawHLine(byte[] buf, int width, int height, int x, int y, int length, byte b, byte g, byte r)
    {
        if (y < 0 || y >= height) return;
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(width, x + length);
        int rowOffset = HeaderSize + (y * width) * 4;
        for (int px = x0; px < x1; px++)
        {
            int idx = rowOffset + px * 4;
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
            buf[idx + 3] = 255;
        }
    }

    public static void DrawVLine(byte[] buf, int width, int height, int x, int y, int length, byte b, byte g, byte r)
    {
        if (x < 0 || x >= width) return;
        int y0 = Math.Max(0, y);
        int y1 = Math.Min(height, y + length);
        for (int py = y0; py < y1; py++)
        {
            int idx = HeaderSize + (py * width + x) * 4;
            buf[idx] = b;
            buf[idx + 1] = g;
            buf[idx + 2] = r;
            buf[idx + 3] = 255;
        }
    }

    public static void SetPixel(byte[] buf, int width, int height, int x, int y, byte b, byte g, byte r)
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
