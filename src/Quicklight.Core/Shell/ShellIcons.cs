using System.Runtime.InteropServices;
using S = Quicklight.Core.Native.Shell;

namespace Quicklight.Core.Shell;

/// <summary>Premultiplied BGRA pixels, top-down.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

public static class ShellIcons
{
    /// <summary>
    /// Gets the shell icon for a path or shell parsing name ("shell:AppsFolder\..."). Call from an STA thread.
    /// </summary>
    /// <param name="thumbnail">A content thumbnail (photo, PDF page, video frame) when the shell has one, else the icon.</param>
    public static IconPixels? Get(string parsingName, int size, bool thumbnail = false)
    {
        var iid = S.IID_IShellItemImageFactory;
        if (S.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) != 0 || obj is null) return null;
        var factory = (S.IShellItemImageFactory)obj;
        try
        {
            if (factory.GetImage(new S.SIZE { cx = size, cy = size }, (thumbnail ? S.SIIGBF.RESIZETOFIT : S.SIIGBF.ICONONLY) | S.SIIGBF.BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return ReadBitmap(hbm); }
            finally { S.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>
    /// The icon at up to <paramref name="size"/> pixels, as its own image: an icon with no big image comes back from the
    /// shell as its small one set in the middle of the requested size (often on opaque black), so it is asked for
    /// again at the size it really has (e.g. 48 px, with transparency). Call from an STA thread.
    /// </summary>
    public static IconPixels? GetLargest(string parsingName, int size)
    {
        var px = Get(parsingName, size);
        return px is not null && NativeSize(px) is { } native && Get(parsingName, native) is { } small ? small : px;
    }

    /// <summary>
    /// The size of the real image when <paramref name="px"/> is a small icon padded out to the requested size (on
    /// transparency or on a flat colour such as the black the shell uses for old icons); null when it fills the canvas.
    /// The background is what lies just inside the edge, which may carry a thin frame.
    /// </summary>
    internal static int? NativeSize(IconPixels px)
    {
        if (px.Width < 64 || px.Height < 64) return null;
        int ring = Math.Max(4, px.Width / 24); // the shell's faint frame runs about 5 px in from the edge at 256 px
        int bg = ((ring + 2) * px.Width + ring + 2) * 4;
        bool Background(int i) => px.Bgra[bg + 3] < 16
            ? px.Bgra[i + 3] <= 32
            : Math.Abs(px.Bgra[i] - px.Bgra[bg]) < 24 && Math.Abs(px.Bgra[i + 1] - px.Bgra[bg + 1]) < 24
              && Math.Abs(px.Bgra[i + 2] - px.Bgra[bg + 2]) < 24 && Math.Abs(px.Bgra[i + 3] - px.Bgra[bg + 3]) < 24;
        int x0 = px.Width, y0 = px.Height, x1 = -1, y1 = -1;
        for (int y = ring; y < px.Height - ring; y++)
            for (int x = ring; x < px.Width - ring; x++)
                if (!Background((y * px.Width + x) * 4))
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < 0) return null;
        int extent = Math.Max(x1 - x0, y1 - y0) + 1;
        return extent < Math.Max(px.Width, px.Height) * 0.6 ? extent : null;
    }

    static IconPixels? ReadBitmap(IntPtr hbm)
    {
        if (S.GetObject(hbm, Marshal.SizeOf<S.BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        var bmi = new S.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<S.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        var hdc = S.GetDC(IntPtr.Zero);
        try
        {
            if (S.GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0) return null;
        }
        finally { S.ReleaseDC(IntPtr.Zero, hdc); }

        // Some icons come back without alpha (all zero); treat those as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
        return new IconPixels(w, h, bits);
    }
}
