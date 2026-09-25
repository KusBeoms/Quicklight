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
