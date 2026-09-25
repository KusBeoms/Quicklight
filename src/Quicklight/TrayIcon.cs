using System.Diagnostics;
using System.Windows;
using Quicklight.Core;
using Forms = System.Windows.Forms;

namespace Quicklight;

public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly QuicklightSettings _settings;

    public TrayIcon(QuicklightSettings settings, Action show, Action reload, Action exit, Action installEverything)
    {
        _settings = settings;
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/quicklight.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "Quicklight",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip();
        var open = menu.Items.Add($"열기 ({settings.Hotkey})", null, (_, _) => show());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("설정 파일 열기", null, (_, _) => OpenFile(QuicklightSettings.DefaultPath));
        menu.Items.Add("설정 다시 불러오기", null, (_, _) => reload());
        menu.Items.Add("로그 보기", null, (_, _) => OpenFile(Log.FilePath));
        var autostart = new Forms.ToolStripMenuItem("Windows 시작 시 실행") { Checked = settings.LaunchAtStartup, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) =>
        {
            _settings.LaunchAtStartup = autostart.Checked;
            _settings.Save();
            Autostart.Sync(autostart.Checked);
        };
        menu.Items.Add(autostart);
        // Shown only while file search has no Everything to use.
        var everything = new Forms.ToolStripMenuItem("파일 검색 엔진(Everything) 설치…", null, (_, _) => installEverything());
        menu.Items.Add(everything);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add($"Quicklight v{BuildInfo.Version} ({BuildInfo.BuildTime} 빌드)").Enabled = false;
        menu.Items.Add("종료", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            open.Text = $"열기 ({_settings.Hotkey})";
            everything.Visible = _settings.FileSearch && Quicklight.Core.Everything.EverythingBootstrap.FindInstalled() is null;
        };
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        _ = PromoteAsync();
    }

    /// <summary>
    /// Windows 11 puts new tray icons in the hidden overflow. Explorer records each icon under
    /// HKCU\Control Panel\NotifyIconSettings; setting IsPromoted shows it on the taskbar. Only done while
    /// the user has not chosen (no IsPromoted value yet), so hiding it later sticks.
    /// </summary>
    static async Task PromoteAsync()
    {
        const string root = @"Control Panel\NotifyIconSettings";
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(1500); // Explorer writes the entry shortly after the icon appears
            try
            {
                using var settings = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root);
                if (settings is null) return; // not Windows 11
                foreach (var name in settings.GetSubKeyNames())
                {
                    using var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\{name}", writable: true);
                    if (entry?.GetValue("ExecutablePath") is not string exe ||
                        !string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("IsPromoted") is null) entry.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Error("tray promotion failed", ex);
                return;
            }
        }
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error("open " + path, ex); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
