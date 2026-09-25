using System.Windows;
using Quicklight.Core;
using Quicklight.Core.Everything;
using Quicklight.Core.Update;

namespace Quicklight;

public partial class App : Application
{
    const string MutexName = @"Local\Quicklight.SingleInstance";
    const string ShowEventName = @"Local\Quicklight.Show";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    SearchEngine? _engine;
    UsageStore? _usage;
    MainWindow? _window;
    HotkeyManager? _hotkey;
    TrayIcon? _tray;
    QuicklightSettings _settings = new();
    UpdateService? _updates;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool showNow = e.Args.Contains("--show");

        // Second launch: ask the running instance to show itself and leave. The event exists before the mutex
        // is taken, so a second launch can never find the mutex without the event.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(true, MutexName, out bool first);
        // Right after an update the previous version is still shutting down: wait for it instead of handing over.
        var updatedFrom = ArgAfter(e.Args, "--updated");
        if (!first && updatedFrom is not null) first = WaitForMutex(_mutex, TimeSpan.FromSeconds(20));
        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("unhandled exception", args.ExceptionObject as Exception);

        _settings = QuicklightSettings.Load();
        Theme.Apply(Resources, _settings.Theme);
        _usage = new UsageStore(UsageStore.DefaultPath);
        var clipboard = new ClipboardHistoryProvider();
        _engine = SearchEngine.CreateDefault(_settings, _usage, [clipboard]);
        _ = _engine.WarmUpAsync();

        _updates = new UpdateService(_settings);
        _window = new MainWindow(_engine, _settings, _updates, clipboard) { Pinned = e.Args.Contains("--pinned") };
        _window.InitializeHidden();

        _hotkey = new HotkeyManager(_window.Handle, () => _window.Toggle());
        RegisterHotkey();

        _tray = new TrayIcon(_settings, show: () => _window.ShowLauncher(), reload: ReloadSettings, exit: () => Shutdown(),
            installEverything: () => _ = SetUpEverythingAsync(userAsked: true));
        Autostart.Sync(_settings.LaunchAtStartup);

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _window.ShowLauncher());
        }) { IsBackground = true, Name = "Show listener" };
        listener.Start();

        // Follow Windows when it switches between light and dark.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
            Dispatcher.BeginInvoke(() =>
            {
                Theme.Apply(Resources, _settings.Theme);
                _window.ApplyBackdrop();
            });
        };

        Log.Info($"started v{Updater.CurrentVersion.ToString(3)}, hotkey {_settings.Hotkey} via {_hotkey.Mode}");
        if (showNow) _window.ShowLauncher();

        // After an update: remove the previous exe (and any leftover download), then say so.
        if (Environment.ProcessPath is { } exe)
            _ = Updater.CleanupAsync(updatedFrom, exe);
        if (updatedFrom is not null) _tray.Notify("Quicklight", $"v{Updater.CurrentVersion.ToString(3)}(으)로 업데이트했습니다.");

        _ = SetUpEverythingAsync(userAsked: false);
        _ = _updates.RunBackgroundAsync(launcherOpen: () => _window.IsVisible);
    }

    static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static bool WaitForMutex(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; } // the old version exited without releasing it: now ours
    }

    bool _everythingSetupRunning;

    /// <summary>
    /// File search needs Everything. Uses an installed one; otherwise asks once whether to install the bundled copy
    /// (one UAC prompt), remembering the answer.
    /// </summary>
    async Task SetUpEverythingAsync(bool userAsked)
    {
        if (_everythingSetupRunning) return; // the tray item while the startup prompt is open
        _everythingSetupRunning = true;
        try
        {
            var outcome = await EverythingBootstrap.EnsureRunningAsync(_settings);
            if (outcome != EverythingBootstrap.Outcome.NeedsSetup || !_settings.BundledEverything) return;
            if (!userAsked && _settings.EverythingSetup != "ask") return;

            var answer = MessageBox.Show(
                "파일 검색에는 Everything이 필요합니다. Quicklight에 들어 있는 Everything을 설치할까요?\n\n" +
                "파일 색인을 읽는 서비스를 등록하느라 관리자 권한 확인(UAC)이 한 번 나옵니다. " +
                "설치 위치: " + EverythingBootstrap.InstallDir,
                "Quicklight", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                MessageBoxOptions.DefaultDesktopOnly); // no owner window while the launcher is hidden: keep it in front
            if (answer != MessageBoxResult.Yes)
            {
                _settings.EverythingSetup = "declined";
                _settings.Save();
                _tray?.Notify("Quicklight", "파일 검색 없이 실행합니다. 트레이 메뉴에서 나중에 설치할 수 있습니다.");
                return;
            }
            bool ok = Environment.ProcessPath is { } exe && await EverythingBootstrap.RequestInstallAsync(exe);
            _settings.EverythingSetup = ok ? "installed" : "declined";
            _settings.Save();
            if (ok) await EverythingBootstrap.EnsureRunningAsync(_settings);
            _tray?.Notify("Quicklight", ok ? "Everything을 설치했습니다. 파일 검색을 쓸 수 있습니다." : "Everything을 설치하지 못했습니다.");
        }
        catch (Exception ex) { Log.Error("Everything setup failed", ex); }
        finally { _everythingSetupRunning = false; }
    }

    bool _releaseForUpdate;

    /// <summary>Called by the launcher once a newer version has been downloaded and swapped in.</summary>
    public void ShutdownForUpdate()
    {
        // The lock is released at the very end of OnExit, after the hotkey, tray, history and translation server are
        // gone, so the new version never runs side by side with this one.
        _releaseForUpdate = true;
        Shutdown();
    }

    void RegisterHotkey()
    {
        if (_hotkey!.Register(_settings.Hotkey, out var error)) return;
        Log.Error($"hotkey {_settings.Hotkey} failed: {error}");
        _tray?.Notify("단축키 등록 실패", $"{_settings.Hotkey}: {error}");
    }

    void ReloadSettings()
    {
        var fresh = QuicklightSettings.Load();
        // Copy into the shared instance so the engine and providers see the change.
        foreach (var prop in typeof(QuicklightSettings).GetProperties().Where(p => p.CanWrite))
            prop.SetValue(_settings, prop.GetValue(fresh));
        Theme.Apply(Resources, _settings.Theme);
        _window?.ApplyBackdrop();
        _hotkey?.Unregister();
        RegisterHotkey();
        Autostart.Sync(_settings.LaunchAtStartup);
        _tray?.Notify("Quicklight", "설정을 다시 불러왔습니다.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _usage?.Flush(); // a save queued by the last launch must not be lost when quitting right after
        _engine?.Dispose();
        if (_releaseForUpdate)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { } // the new version is waiting for it
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
