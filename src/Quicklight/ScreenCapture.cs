using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Quicklight;

internal static class ScreenCapture
{
    /// <summary>
    /// Copies a screen rectangle (physical pixels) into a frozen bitmap whose DPI matches <paramref name="scale"/>,
    /// so it lays out at the same size in DIPs as the area it was taken from.
    /// </summary>
    public static BitmapSource Capture(int x, int y, int width, int height, double scale)
    {
        using var bmp = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);

        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, width, height), Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            var source = BitmapSource.Create(width, height, 96 * scale, 96 * scale, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * height, data.Stride);
            source.Freeze();
            return source;
        }
        finally { bmp.UnlockBits(data); }
    }
}
