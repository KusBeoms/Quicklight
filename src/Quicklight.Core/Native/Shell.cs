using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Quicklight.Core.Native;

internal static class Shell
{
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [Flags]
    public enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        ICONONLY = 0x04,
        ICONBACKGROUND = 0x80,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
    }

    /// <summary>Where a .lnk points and with which arguments. Target is null for shortcuts to non-file items (and on failure).</summary>
    public static (string? Target, string? Arguments) ReadShortcut(string lnk)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            var sl = (IShellLinkW)link;
            var path = new System.Text.StringBuilder(1024);
            sl.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new System.Text.StringBuilder(2048);
            sl.GetArguments(args, args.Capacity);
            return (path.Length > 0 ? path.ToString() : null, args.Length > 0 ? args.ToString() : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or IOException) { return (null, null); }
        finally { if (link is not null) Marshal.ReleaseComObject(link); }
    }

    static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([In] ref Guid rfid, uint flags, IntPtr hToken, out IntPtr path);

    /// <summary>The user's Downloads folder, wherever it was moved to.</summary>
    public static string? DownloadsFolder()
    {
        var id = FOLDERID_Downloads;
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { CoTaskMemFree(p); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr h, int c, out BITMAP bmp);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

}
