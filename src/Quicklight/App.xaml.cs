using System.Windows;
using Quicklight.Core;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool showNow = e.Args.Contains("--show");

        // Second launch: ask the running instance to show itself and leave. The event exists before the mutex
        // is taken, so a second launch can never find the mutex without the event.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(true, MutexName, out bool first);
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
        _engine = SearchEngine.CreateDefault(_settings, _usage);
        _ = _engine.WarmUpAsync();

        _window = new MainWindow(_engine, _settings) { Pinned = e.Args.Contains("--pinned") };
        _window.InitializeHidden();

        _hotkey = new HotkeyManager(_window.Handle, () => _window.Toggle());
        RegisterHotkey();

        _tray = new TrayIcon(_settings, show: () => _window.ShowLauncher(), reload: ReloadSettings, exit: () => Shutdown());
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

        Log.Info($"started, hotkey {_settings.Hotkey} via {_hotkey.Mode}");
        if (showNow) _window.ShowLauncher();
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
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
