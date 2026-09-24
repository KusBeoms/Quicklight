using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Quicklight;

internal static class ScreenCapture
{
    const int Downscale = 4; // the blur hides the detail anyway; a quarter-size bitmap blurs and draws much faster

    /// <summary>
    /// Copies a screen rectangle (physical pixels) and blurs it once, into a small frozen bitmap whose DPI makes it
    /// lay out at the same size in DIPs as the area it was taken from (WPF scales it up smoothly).
    /// Blurring here instead of with a live BlurEffect keeps every animation frame cheap.
    /// </summary>
    /// <param name="sigmaDips">Gaussian blur strength in DIPs.</param>
    public static BitmapSource CaptureBlurred(int x, int y, int width, int height, double scale, double sigmaDips)
    {
        using var bmp = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);

        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, width, height), Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
        byte[] pixels = new byte[data.Stride * height];
        int stride = data.Stride;
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bmp.UnlockBits(data); }

        // Average each Downscale x Downscale block into one pixel, per channel (B, G, R).
        int w = Math.Max(1, width / Downscale), h = Math.Max(1, height / Downscale);
        var planes = new float[3][];
        for (int c = 0; c < 3; c++) planes[c] = new float[w * h];
        const float inv = 1f / (Downscale * Downscale);
        for (int sy = 0; sy < h; sy++)
        for (int sx = 0; sx < w; sx++)
        {
            float b = 0, gr = 0, r = 0;
            for (int dy = 0; dy < Downscale; dy++)
            {
                int row = (sy * Downscale + dy) * stride + sx * Downscale * 4;
                for (int dx = 0; dx < Downscale; dx++, row += 4)
                {
                    b += pixels[row];
                    gr += pixels[row + 1];
                    r += pixels[row + 2];
                }
            }
            int i = sy * w + sx;
            planes[0][i] = b * inv;
            planes[1][i] = gr * inv;
            planes[2][i] = r * inv;
        }

        // Three box blurs approximate a Gaussian: each pass adds ((2r+1)^2 - 1) / 12 of variance.
        double sigma = sigmaDips * scale / Downscale;
        int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
        var tmp = new float[w * h];
        foreach (var plane in planes)
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(plane, tmp, w, h, radius, horizontal: true);
                BoxBlur(tmp, plane, w, h, radius, horizontal: false);
            }

        var output = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            output[i * 4] = (byte)Math.Clamp(planes[0][i] + 0.5f, 0, 255);
            output[i * 4 + 1] = (byte)Math.Clamp(planes[1][i] + 0.5f, 0, 255);
            output[i * 4 + 2] = (byte)Math.Clamp(planes[2][i] + 0.5f, 0, 255);
        }
        double dpi = 96 * scale / Downscale;
        var source = BitmapSource.Create(w, h, dpi * w * Downscale / width, dpi * h * Downscale / height,
            PixelFormats.Bgr32, null, output, w * 4);
        source.Freeze();
        return source;
    }

    /// <summary>Running-sum box blur along rows or columns; edges repeat the border pixel.</summary>
    static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        int step = horizontal ? 1 : w;
        float inv = 1f / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w : line;
            float sum = src[start] * (r + 1);
            for (int k = 1; k <= r; k++) sum += src[start + Math.Min(k, length - 1) * step];
            for (int k = 0; k < length; k++)
            {
                dst[start + k * step] = sum * inv;
                int add = start + Math.Min(k + r + 1, length - 1) * step;
                int remove = start + Math.Max(k - r, 0) * step;
                sum += src[add] - src[remove];
            }
        }
    }
}
