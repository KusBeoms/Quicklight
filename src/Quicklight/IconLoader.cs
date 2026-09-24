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

    void Run()
    {
        foreach (var (source, key, generation, done) in _queue.GetConsumingEnumerable())
        {
            if (generation != Volatile.Read(ref _generation)) continue; // stale: those rows are gone
            if (!_cache.TryGetValue(key, out var img))
            {
                try
                {
                    var px = ShellIcons.Get(source, _size);
                    if (px is not null)
                    {
                        var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32, null, px.Bgra, px.Width * 4);
                        bmp.Freeze();
                        img = bmp;
                    }
                }
                catch (Exception ex) { Log.Error("icon " + source, ex); }
                _cache[key] = img;
            }
            done(img);
        }
    }
}
