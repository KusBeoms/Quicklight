using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Quicklight.Core;
using Quicklight.Core.Shell;

namespace Quicklight;

/// <summary>
/// Loads shell icons on a dedicated STA thread and caches them. Plain documents share one icon per extension;
/// executables, shortcuts and apps are cached per item.
/// </summary>
public sealed class IconLoader
{
    static readonly HashSet<string> PerItemExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr", "" };

    readonly ConcurrentDictionary<string, ImageSource?> _cache = new();
    readonly BlockingCollection<(string Source, string Key, int Generation, Action<ImageSource?> Done)> _queue = new();
    readonly int _size;
    int _generation;

    /// <summary>Starts a new batch; requests from older batches (results already replaced) are skipped.</summary>
    public int NextGeneration() => Interlocked.Increment(ref _generation);

    public IconLoader(int size)
    {
        _size = size;
        var t = new Thread(Run) { IsBackground = true, Name = "Icon loader" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Calls <paramref name="done"/> on the loader thread (or synchronously when cached). Frozen images are thread-safe.</summary>
    public void Load(string source, bool isFolder, int generation, Action<ImageSource?> done)
    {
        var key = CacheKey(source, isFolder);
        if (_cache.TryGetValue(key, out var cached)) { done(cached); return; }
        _queue.Add((source, key, generation, done));
    }

    static string CacheKey(string source, bool isFolder)
    {
        if (source.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return source;
        // Drive roots and special folders have their own icons; ordinary folders share one.
        if (isFolder) return source.Length <= 3 ? source : "<folder>";
        var ext = Path.GetExtension(source);
        return PerItemExtensions.Contains(ext) ? source : "<ext>" + ext;
    }

    const int SourceSize = 256; // the "jumbo" icon most apps ship

    /// <summary>
    /// The icon at <paramref name="size"/> pixels, high-quality (Fant) scaled. An icon drawn small on its canvas (an
    /// old program with only a 32 or 48 px image, set in the middle of the 256 px one) is cut to what is drawn and
    /// blown up, so every tile looks about as big; blurrier, but not tiny.
    /// </summary>
    static BitmapSource Fit(IconPixels px, int size)
    {
        BitmapSource src = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4);
        var (x0, y0, x1, y1) = Drawn(px);
        int extent = Math.Max(x1 - x0, y1 - y0) + 1;
        if (extent > 0 && extent < Math.Max(px.Width, px.Height) * 0.7)
        {
            int side = Math.Min(Math.Min(px.Width, px.Height), (int)Math.Ceiling(extent * 1.12)); // a little margin, like a designed icon
            int x = Math.Clamp((x0 + x1) / 2 - side / 2, 0, px.Width - side), y = Math.Clamp((y0 + y1) / 2 - side / 2, 0, px.Height - side);
            src = new CroppedBitmap(src, new System.Windows.Int32Rect(x, y, side, side));
        }
        if (src.PixelWidth != size || src.PixelHeight != size)
        {
            double scale = (double)size / Math.Max(src.PixelWidth, src.PixelHeight);
            int w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen()) dc.DrawImage(src, new System.Windows.Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            src = target;
        }
        src.Freeze();
        return src;
    }

    /// <summary>The box around the pixels that are not (nearly) transparent; the whole image when none are.</summary>
    static (int X0, int Y0, int X1, int Y1) Drawn(IconPixels px)
    {
        int x0 = px.Width, y0 = px.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < px.Height; y++)
            for (int x = 0; x < px.Width; x++)
                if (px.Bgra[(y * px.Width + x) * 4 + 3] > 16)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        return x1 < 0 ? (0, 0, px.Width - 1, px.Height - 1) : (x0, y0, x1, y1);
    }

    void Run()
    {
        foreach (var (source, key, generation, done) in _queue.GetConsumingEnumerable())
        {
            if (generation != Volatile.Read(ref _generation)) continue; // stale: those rows are gone
            if (!_cache.TryGetValue(key, out var img))
            {
                try
                {
                    // Ask for the 256 px image and shrink it ourselves: asked for 64, the shell often blows up a 48 px image.
                    var px = ShellIcons.GetLargest(source, SourceSize);
                    if (px is not null)
                        img = Fit(px, _size);
                }
                catch (Exception ex) { Log.Error("icon " + source, ex); }
                _cache[key] = img;
            }
            done(img);
        }
    }
}
