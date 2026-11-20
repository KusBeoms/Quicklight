using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using Quicklight.Core;
using Quicklight.Core.Ai;
using Quicklight.Core.Integration;
using Drawing = System.Drawing;

namespace Quicklight.Siri;

/// <summary>What the user was looking at when they opened Quicklight: the window in front, the files or text they had selected.</summary>
public sealed record LauncherContext(IntPtr Window, string? Title, string? Program, IReadOnlyList<string> Files, string? Text);

/// <summary>
/// Tools for the questions that point at something: "이 파일 요약해줘", "이 창에 뭐라고 써 있어?", "화면 봐 줘". The AI decides
/// when to use them (nothing is read until it asks); both only read, so they run without a prompt.
/// </summary>
public sealed class ScreenTools(Func<LauncherContext?> context) : IAgentTools
{
    public IReadOnlyList<AgentTool> Tools { get; } =
    [
        new AgentTool("get_context",
            "The user's context from the moment they opened Quicklight: the window that was in front (title and program), the files selected in File Explorer, and the text selected in that app. Use it when the user says 'this', 'this file', 'the selected text' or similar.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, ReadOnly: true),
        new AgentTool("look_at_screen",
            "Captures the window the user was working in (or the whole screen) and returns the text recognized in it; when the AI model can see pictures, the picture is attached too. Use it when the user asks about what is on the screen, in this window, or about an error or page they are looking at.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("window", "screen"), ["description"] = "'window' (default): the window the user was in. 'screen': the whole monitor it is on." },
                },
            }, ReadOnly: true),
    ];

    public string Describe(string name, JsonObject args) => name == "look_at_screen" ? "화면 보기" : "선택한 항목 확인";

    public async Task<string> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        var ctx = context();
        if (ctx is null) return "사용자가 연 시점의 정보가 없습니다.";
        if (name == "get_context") return Describe(ctx);
        if (name != "look_at_screen") return "알 수 없는 도구입니다.";
        bool screen = args["target"]?.GetValue<string>() == "screen";
        return await Task.Run(() => Look(ctx, screen), ct);
    }

    static string Describe(LauncherContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"앞에 있던 창: {c.Title ?? "(알 수 없음)"}" + (c.Program is null ? "" : $" ({c.Program})"));
        if (c.Files.Count > 0)
        {
            sb.AppendLine("탐색기에서 선택한 파일:");
            foreach (var f in c.Files.Take(50)) sb.AppendLine("- " + f);
        }
        if (!string.IsNullOrWhiteSpace(c.Text)) sb.AppendLine("선택한 글:\n" + c.Text);
        if (c.Files.Count == 0 && string.IsNullOrWhiteSpace(c.Text)) sb.AppendLine("선택한 파일이나 글은 없습니다.");
        return sb.ToString();
    }

    // ---------- the screen ----------

    static string Look(LauncherContext c, bool screen)
    {
        try
        {
            using var bmp = screen ? CaptureScreen(c.Window) : CaptureWindow(c.Window);
            if (bmp is null) return "화면을 캡처하지 못했습니다.";
            Directory.CreateDirectory(CaptureDir);
            var path = Path.Combine(CaptureDir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            bmp.Save(path, Drawing.Imaging.ImageFormat.Png);
            var text = Ocr(path);
            var sb = new StringBuilder();
            sb.AppendLine(screen ? "화면을 캡처했습니다." : $"창 '{c.Title}'을 캡처했습니다.");
            sb.AppendLine("IMAGE_FILE:" + path);
            sb.AppendLine(text.Length > 0 ? "인식한 글자:\n" + text : "인식한 글자가 없습니다.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("look_at_screen failed", ex);
            return "화면을 보지 못했습니다: " + ex.Message;
        }
    }

    public static string CaptureDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quicklight", "captures");

    /// <summary>Captures go after two days (normally right after use).</summary>
    public static void PurgeOldCaptures()
    {
        try
        {
            if (!Directory.Exists(CaptureDir)) return;
            foreach (var f in Directory.EnumerateFiles(CaptureDir, "capture-*.png").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-2))) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static Drawing.Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r) || r.Right - r.Left < 8) return null;
        var bmp = new Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using var g = Drawing.Graphics.FromImage(bmp);
        var dc = g.GetHdc();
        // PW_RENDERFULLCONTENT: the window's own rendering, so another window or Quicklight in front does not end up in it.
        bool ok = PrintWindow(hwnd, dc, 2);
        g.ReleaseHdc(dc);
        if (!ok) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        return bmp;
    }

    static Drawing.Bitmap? CaptureScreen(IntPtr hwnd)
    {
        var screen = hwnd != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(hwnd) : System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return null;
        var b = screen.Bounds;
        var bmp = new Drawing.Bitmap(b.Width, b.Height);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
        return bmp;
    }

    static string Ocr(string path)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return "";
            using var stream = File.OpenRead(path);
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
            // The engine refuses pictures larger than MaxImageDimension (4K captures): scale down to fit.
            double k = Math.Min(1.0, (double)Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            using var bitmap = k < 1.0
                ? decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, new Windows.Graphics.Imaging.BitmapTransform
                    { ScaledWidth = (uint)(decoder.PixelWidth * k), ScaledHeight = (uint)(decoder.PixelHeight * k) },
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult()
                : decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
            var text = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult().Text;
            return text.Length > 3000 ? text[..3000] : text;
        }
        catch (Exception ex)
        {
            Log.Error("ocr failed", ex);
            return "";
        }
    }

    // ---------- the context, read before the launcher takes the focus ----------

    /// <summary>
    /// What the window in front holds right now. Called as the launcher opens, before it takes the focus: afterwards the
    /// selection in that app is no longer the focused one.
    /// </summary>
    public static LauncherContext Capture(IntPtr foreground)
    {
        string? title = null, program = null;
        if (foreground != IntPtr.Zero)
        {
            var sb = new StringBuilder(256);
            GetWindowText(foreground, sb, sb.Capacity);
            title = sb.Length > 0 ? sb.ToString() : null;
            GetWindowThreadProcessId(foreground, out uint pid);
            try { using var p = Process.GetProcessById((int)pid); program = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        var text = FocusedSelection.GetSelectedText(); // first: the explorer query can take seconds, by when the focus has moved on
        var files = ExplorerSelection.GetSelectedPaths(foreground);
        return new LauncherContext(foreground, title, program, files, text);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
