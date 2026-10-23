using CamScope.Streaming.Rendering;
using Xunit;

namespace CamScope.UnitTests.Streaming;

public class SyntheticFrameGeneratorTests
{
    [Theory]
    [InlineData("[DEMO] Front Entrance Dome")]
    [InlineData("[DEMO] Loading Dock PTZ")]
    [InlineData("[DEMO] Server Room Fixed")]
    [InlineData("[DEMO] Corporate Reception Lobby")]
    [InlineData("[DEMO] Parking Lot 4K Bullet")]
    public void GenerateBmpFrame_DemoCameras_ProduceValidBmpHeader(string cameraName)
    {
        var frame = SyntheticFrameGenerator.GenerateBmpFrame(cameraName, "192.168.1.100", 1);

        Assert.NotNull(frame);
        Assert.Equal(691254, frame.Length);

        // BMP magic signature 'BM'
        Assert.Equal(0x42, frame[0]);
        Assert.Equal(0x4D, frame[1]);

        // File size in header matches array length
        int fileSize = BitConverter.ToInt32(frame, 2);
        Assert.Equal(691254, fileSize);

        // Offset to pixels is 54
        int offset = BitConverter.ToInt32(frame, 10);
        Assert.Equal(54, offset);

        // DIB header size is 40
        int dibHeaderSize = BitConverter.ToInt32(frame, 14);
        Assert.Equal(40, dibHeaderSize);

        // Width is 640
        int width = BitConverter.ToInt32(frame, 18);
        Assert.Equal(640, width);

        // Height is -360 (top-down)
        int height = BitConverter.ToInt32(frame, 22);
        Assert.Equal(-360, height);

        // 24 bits per pixel
        short bpp = BitConverter.ToInt16(frame, 28);
        Assert.Equal(24, bpp);
    }

    [Fact]
    public void GenerateBmpFrame_SuccessiveFrames_ProduceDistinctBytes()
    {
        var frame1 = SyntheticFrameGenerator.GenerateBmpFrame("[DEMO] Front Entrance Dome", "192.168.1.101", 1);
        var frame2 = SyntheticFrameGenerator.GenerateBmpFrame("[DEMO] Front Entrance Dome", "192.168.1.101", 50);

        Assert.Equal(frame1.Length, frame2.Length);
        // Due to moving AI bounding boxes and live timestamp, frames must differ
        Assert.False(frame1.SequenceEqual(frame2));
    }

    [Fact]
    public void GenerateBmpFrame_CustomResolution_GeneratesValidFallbackBmp()
    {
        int customWidth = 320;
        int customHeight = 240;
        int expectedImageSize = customWidth * customHeight * 3;
        int expectedTotalSize = 54 + expectedImageSize;

        var frame = SyntheticFrameGenerator.GenerateBmpFrame("Custom Camera", "10.0.0.1", 0, customWidth, customHeight);

        Assert.NotNull(frame);
        Assert.Equal(expectedTotalSize, frame.Length);
        Assert.Equal(0x42, frame[0]);
        Assert.Equal(0x4D, frame[1]);
        Assert.Equal(customWidth, BitConverter.ToInt32(frame, 18));
        Assert.Equal(-customHeight, BitConverter.ToInt32(frame, 22));
    }
}
