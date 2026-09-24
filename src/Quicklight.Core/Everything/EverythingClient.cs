using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Quicklight.Core.Native;

namespace Quicklight.Core.Everything;

public enum EverythingSort : uint
{
    NameAscending = 1,
    PathAscending = 3,
    SizeDescending = 6,
    DateModifiedDescending = 14,
    RunCountDescending = 20,
    DateRecentlyChangedDescending = 22,
    DateRunDescending = 26,
}

public sealed record EverythingItem(string Name, string Directory, bool IsFolder, long Size, DateTime? Modified)
{
    public string FullPath => string.IsNullOrEmpty(Directory) ? Name : System.IO.Path.Combine(Directory, Name);
}

public sealed record EverythingQuery(string Search, int MaxResults = 50, EverythingSort Sort = EverythingSort.NameAscending,
    bool MatchPath = false, bool MatchCase = false, bool Regex = false);

/// <summary>
/// Talks to a running Everything (voidtools) instance through its WM_COPYDATA IPC (EVERYTHING_IPC_QUERY2),
/// so no SDK DLL is needed. A dedicated thread owns a message-only window that receives the replies.
/// </summary>
public sealed class EverythingClient : IDisposable
{
    const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    const uint CopyDataQuery2W = 18;
    const uint ReplyIdBase = 0x514C_0000; // + a sequence number, echoed back by Everything

    const uint SearchFlagMatchCase = 0x1, SearchFlagMatchPath = 0x4, SearchFlagRegex = 0x8;
    const uint RequestName = 0x1, RequestPath = 0x2, RequestSize = 0x10, RequestDateModified = 0x40;
    const uint ItemFolder = 0x1, ItemDrive = 0x2;

    readonly ConcurrentQueue<Request> _queue = new();
    readonly AutoResetEvent _wake = new(false);
    volatile bool _closed;
    readonly Thread _thread;
    readonly TimeSpan _timeout;
    Win32.WndProc? _wndProc; // kept alive for the window's lifetime
    IntPtr _hwnd;
    byte[]? _reply;
    uint _expectedReply;
    ushort _sequence;
    static int _instances;
    DateTime _backoffUntil = DateTime.MinValue;
    static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);

    sealed record Request(EverythingQuery Query, TaskCompletionSource<IReadOnlyList<EverythingItem>> Tcs, CancellationToken Token);

    readonly bool _failFast;

    /// <param name="timeout">How long one query may take, including Everything's reply.</param>
    /// <param name="failFast">
    /// True for the interactive launcher: give up at once when Windows reports Everything as hung and skip file search
    /// for a few seconds afterwards. False for explicit requests (MCP), which wait up to <paramref name="timeout"/>.
    /// </param>
    public EverythingClient(TimeSpan? timeout = null, bool failFast = true)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _failFast = failFast;
        _thread = new Thread(Run) { IsBackground = true, Name = "Everything IPC" };
        _thread.Start();
    }

    /// <summary>True when an Everything window accepting IPC exists.</summary>
    public static bool IsAvailable => FindEverythingWindow() != IntPtr.Zero;

    /// <summary>Everything's major.minor.revision if it answers IPC within <paramref name="timeoutMs"/>, else null (not running or hung).</summary>
    public static Version? GetVersion(uint timeoutMs = 500)
    {
        var h = FindEverythingWindow();
        if (h == IntPtr.Zero) return null;
        var parts = new int[3];
        for (int i = 0; i < 3; i++)
        {
            // EVERYTHING_WM_IPC (WM_USER) with EVERYTHING_IPC_GET_MAJOR/MINOR/REVISION_VERSION = 0/1/2.
            if (Win32.SendMessageTimeout(h, 0x0400, (IntPtr)i, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, timeoutMs, out var r) == IntPtr.Zero) return null;
            parts[i] = (int)r;
        }
        return new Version(parts[0], parts[1], parts[2]);
    }

    static IntPtr FindEverythingWindow()
    {
        var h = Win32.FindWindow(IpcWindowClass, null);
        // Everything 1.5 alpha uses an instance-suffixed class name.
        if (h == IntPtr.Zero) h = Win32.FindWindow(IpcWindowClass + "_(1.5a)", null);
        return h;
    }

    public Task<IReadOnlyList<EverythingItem>> SearchAsync(EverythingQuery query, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<EverythingItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) { tcs.SetCanceled(ct); return tcs.Task; }
        if (_closed) { tcs.SetException(new ObjectDisposedException(nameof(EverythingClient))); return tcs.Task; }
        _queue.Enqueue(new Request(query, tcs, ct));
        _wake.Set();
        return tcs.Task;
    }

    void Run()
    {
        _wndProc = WndProc;
        var cls = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Win32.GetModuleHandle(null),
            // One class per client: a shared class would route every reply to the first client's WndProc.
            lpszClassName = $"QuicklightEverythingReply_{Environment.ProcessId}_{Interlocked.Increment(ref _instances)}",
        };
        Win32.RegisterClassEx(ref cls);
        _hwnd = Win32.CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);

        // Keep pumping messages while idle too: a late reply to an abandoned query is a sent WM_COPYDATA,
        // and Everything's own UI thread waits until we process it.
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_closed)
        {
            Win32.MsgWaitForMultipleObjects(1, handles, false, 0xFFFFFFFF, Win32.QS_ALLINPUT);
            PumpMessages();
            while (!_closed && _queue.TryDequeue(out var req))
            {
                if (req.Token.IsCancellationRequested) { req.Tcs.TrySetCanceled(req.Token); continue; }
                try { req.Tcs.TrySetResult(Execute(req.Query, req.Token)); }
                catch (OperationCanceledException) { req.Tcs.TrySetCanceled(req.Token); }
                catch (Exception ex) { req.Tcs.TrySetException(ex); }
            }
        }
        while (_queue.TryDequeue(out var left)) left.Tcs.TrySetException(new ObjectDisposedException(nameof(EverythingClient)));
        if (_hwnd != IntPtr.Zero) Win32.DestroyWindow(_hwnd);
        Win32.UnregisterClass(cls.lpszClassName, cls.hInstance);
    }

    IReadOnlyList<EverythingItem> Execute(EverythingQuery q, CancellationToken ct)
    {
        // A hung Everything must not stall every keystroke: after a failure, fail fast for a while.
        if (_failFast && DateTime.UtcNow < _backoffUntil) throw new EverythingUnavailableException("Everything did not respond recently; retrying shortly.");
        var target = FindEverythingWindow();
        if (target == IntPtr.Zero) throw new EverythingUnavailableException();
        try { return Query(target, q, ct); }
        catch (EverythingUnavailableException)
        {
            // Hung or refusing. A slow reply (TimeoutException) is not a reason to stop asking.
            _backoffUntil = DateTime.UtcNow + Backoff;
            throw;
        }
    }

    IReadOnlyList<EverythingItem> Query(IntPtr target, EverythingQuery q, CancellationToken ct)
    {

        // EVERYTHING_IPC_QUERY2: 7 DWORDs followed by the null-terminated UTF-16 search string.
        var search = Encoding.Unicode.GetBytes(q.Search + "\0");
        var buf = new byte[28 + search.Length];
        uint flags = (q.MatchCase ? SearchFlagMatchCase : 0) | (q.MatchPath ? SearchFlagMatchPath : 0) | (q.Regex ? SearchFlagRegex : 0);
        BitConverter.TryWriteBytes(buf.AsSpan(0), (uint)_hwnd.ToInt64());
        // A fresh id per query, so a late reply to an abandoned query is never taken for this one.
        _expectedReply = ReplyIdBase + ++_sequence;
        BitConverter.TryWriteBytes(buf.AsSpan(4), _expectedReply);
        BitConverter.TryWriteBytes(buf.AsSpan(8), flags);
        BitConverter.TryWriteBytes(buf.AsSpan(12), 0u);
        BitConverter.TryWriteBytes(buf.AsSpan(16), (uint)Math.Clamp(q.MaxResults, 1, 1000));
        BitConverter.TryWriteBytes(buf.AsSpan(20), RequestName | RequestPath | RequestSize | RequestDateModified);
        BitConverter.TryWriteBytes(buf.AsSpan(24), (uint)q.Sort);
        search.CopyTo(buf, 28);

        _reply = null;
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            var cds = new Win32.COPYDATASTRUCT { dwData = (IntPtr)CopyDataQuery2W, cbData = buf.Length, lpData = handle.AddrOfPinnedObject() };
            var sent = Win32.SendMessageTimeout(target, Win32.WM_COPYDATA, _hwnd, ref cds,
                _failFast ? Win32.SMTO_ABORTIFHUNG : 0, (uint)_timeout.TotalMilliseconds, out var ok);
            if (sent == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new EverythingUnavailableException(err is 1460 or 0 ? "Everything is not responding (its window is hung)." : $"SendMessage failed, Win32 error {err}.");
            }
            if (ok == IntPtr.Zero) throw new EverythingUnavailableException("Everything rejected the query (IPC disabled or database not loaded)");
        }
        finally { handle.Free(); }

        // The reply arrives as a WM_COPYDATA sent to our window; pump until it does.
        var deadline = DateTime.UtcNow + _timeout;
        while (_reply is null)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Everything did not reply in time.");
            Win32.MsgWaitForMultipleObjects(0, null, false, (uint)Math.Min(50, remaining.TotalMilliseconds), Win32.QS_ALLINPUT);
            PumpMessages();
        }
        return ParseList2(_reply);
    }

    static void PumpMessages()
    {
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<Win32.COPYDATASTRUCT>(lParam);
            if ((uint)cds.dwData == _expectedReply && cds.cbData > 0)
            {
                _expectedReply = 0;
                var data = new byte[cds.cbData];
                Marshal.Copy(cds.lpData, data, 0, cds.cbData);
                _reply = data;
                return (IntPtr)1;
            }
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Parses EVERYTHING_IPC_LIST2. Per-item data appears in request-flag bit order: name, path, size, date modified.</summary>
    internal static IReadOnlyList<EverythingItem> ParseList2(byte[] data)
    {
        // The reply comes from another process: validate every offset instead of trusting it.
        var span = data.AsSpan();
        const int header = 20, itemSize = 8;
        if (span.Length < header) return [];
        uint numItems = Math.Min(BitConverter.ToUInt32(span[4..]), (uint)((span.Length - header) / itemSize));
        uint requestFlags = BitConverter.ToUInt32(span[12..]);
        var items = new List<EverythingItem>((int)numItems);
        for (int i = 0; i < numItems; i++)
        {
            int itemOffset = header + i * itemSize;
            uint itemFlags = BitConverter.ToUInt32(span[itemOffset..]);
            uint offset = BitConverter.ToUInt32(span[(itemOffset + 4)..]);
            if (offset >= span.Length) break;
            int p = (int)offset;
            try
            {

            string name = "", path = "";
            long size = -1;
            DateTime? modified = null;
            if ((requestFlags & RequestName) != 0) name = ReadString(span, ref p);
            if ((requestFlags & RequestPath) != 0) path = ReadString(span, ref p);
            if ((requestFlags & 0x4) != 0) ReadString(span, ref p);  // full path (not requested)
            if ((requestFlags & 0x8) != 0) ReadString(span, ref p);  // extension (not requested)
            if ((requestFlags & RequestSize) != 0) { size = BitConverter.ToInt64(span[p..]); p += 8; }
            if ((requestFlags & 0x20) != 0) p += 8;                   // date created (not requested)
            if ((requestFlags & RequestDateModified) != 0)
            {
                long ft = BitConverter.ToInt64(span[p..]); p += 8;
                if (ft > 0 && ft != -1) { try { modified = DateTime.FromFileTime(ft); } catch (ArgumentOutOfRangeException) { } }
            }

            bool isFolder = (itemFlags & (ItemFolder | ItemDrive)) != 0;
            // Drives come back as name "C:" with an empty path.
            if ((itemFlags & ItemDrive) != 0 && !name.EndsWith('\\')) name += "\\";
            items.Add(new EverythingItem(name, path, isFolder, isFolder ? -1 : size, modified));
            }
            catch (ArgumentOutOfRangeException) { break; } // truncated or malformed item
        }
        return items;
    }

    static string ReadString(ReadOnlySpan<byte> span, ref int p)
    {
        uint len = BitConverter.ToUInt32(span[p..]);
        p += 4;
        if (len > (uint)(span.Length - p) / 2) throw new ArgumentOutOfRangeException(nameof(span), "string runs past the reply");
        var s = Encoding.Unicode.GetString(span.Slice(p, (int)len * 2));
        p += ((int)len + 1) * 2; // null terminator
        return s;
    }

    public void Dispose()
    {
        _closed = true;
        _wake.Set();
    }
}

public sealed class EverythingUnavailableException(string? detail = null)
    : Exception("Everything is not running or not reachable. Start Everything (voidtools.com) to enable file search." + (detail is null ? "" : " " + detail));
