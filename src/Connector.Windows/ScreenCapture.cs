using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Connector.Windows;

public static class ScreenCapture
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static byte[] CapturePrimaryScreenToJpeg(int quality = 80)
    {
        quality = Math.Clamp(quality, 1, 100);

        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            width = 1920;
            height = 1080;
        }
        var bounds = new Rectangle(0, 0, width, height);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            try
            {
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            catch
            {
                graphics.Clear(Color.FromArgb(30, 30, 30));
                using var brush = new SolidBrush(Color.WhiteSmoke);
                using var font = new Font(FontFamily.GenericSansSerif, 20, FontStyle.Bold);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString(
                    "⚠️ 屏幕捕获受限（当前可能处于锁屏或无活动显示器状态）",
                    font,
                    brush,
                    new RectangleF(0, 0, bounds.Width, bounds.Height),
                    sf);
            }
        }

        using var ms = new MemoryStream();
        var jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        if (jpegCodec != null)
        {
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(ms, jpegCodec, encoderParams);
        }
        else
        {
            bitmap.Save(ms, ImageFormat.Jpeg);
        }

        return ms.ToArray();
    }
}
