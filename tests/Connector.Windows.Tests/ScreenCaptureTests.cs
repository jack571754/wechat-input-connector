using Connector.Windows;
using Xunit;

namespace Connector.Windows.Tests;

public sealed class ScreenCaptureTests
{
    [Fact]
    public void CapturePrimaryScreenToJpeg_ReturnsValidJpegBytes()
    {
        var bytes = ScreenCapture.CapturePrimaryScreenToJpeg();

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100, $"Captured image is too small: {bytes.Length} bytes");
        // JPEG magic number: 0xFF, 0xD8
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(90)]
    public void CapturePrimaryScreenToJpeg_WithSpecificQuality_ReturnsValidJpeg(int quality)
    {
        var bytes = ScreenCapture.CapturePrimaryScreenToJpeg(quality);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(150)]
    public void CapturePrimaryScreenToJpeg_WithOutOfRangeQuality_ClampsAndSucceeds(int quality)
    {
        var bytes = ScreenCapture.CapturePrimaryScreenToJpeg(quality);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }
}
