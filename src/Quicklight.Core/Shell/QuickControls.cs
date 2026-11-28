using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Quicklight.Core.Shell;

/// <summary>"볼륨 30", "다크 모드", "블루투스 끄기" and friends: a pure parser plus the native calls that carry them out.</summary>
public static class QuickControls
{
    public const string Prefix = "ctl:";

    static readonly Regex Vol = new(@"^(?:볼륨|소리|사운드|volume|vol)\s*([+-]?\d{1,3})\s*%?$", RegexOptions.IgnoreCase);
    static readonly Regex RadioRx = new(@"^(블루투스|bluetooth|와이파이|wi-?fi)\s*(끄기|꺼|켜기|켜|off|on)$", RegexOptions.IgnoreCase);

    /// <summary>Returns (command id, title, subtitle) when the whole query is a quick control with a clear value; otherwise null (plain "볼륨" stays a settings page).</summary>
    public static (string Id, string Title, string Subtitle)? Parse(string text)
    {
        var q = Regex.Replace(text.Trim(), @"\s+", " ");
        if (Vol.Match(q) is { Success: true } v)
        {
            string s = v.Groups[1].Value;
            int n = int.Parse(s);
            bool rel = s[0] is '+' or '-';
            if (!rel && n is < 0 or > 100) return null;
            return (Prefix + "vol:" + s, rel ? $"볼륨 {s}" : $"볼륨 {n}%", "시스템 볼륨 조절");
        }
        switch (q.ToLowerInvariant())
        {
            case "음소거" or "mute": return (Prefix + "mute", "음소거", "음소거 켜기/끄기");
            case "음소거 해제" or "unmute": return (Prefix + "unmute", "음소거 해제", "시스템 볼륨 조절");
            case "다크 모드" or "다크모드" or "dark mode": return (Prefix + "dark", "다크 모드", "앱과 시스템 테마");
            case "라이트 모드" or "라이트모드" or "light mode": return (Prefix + "light", "라이트 모드", "앱과 시스템 테마");
        }
        if (RadioRx.Match(q) is { Success: true } r)
        {
            bool bt = r.Groups[1].Value.ToLowerInvariant() is "블루투스" or "bluetooth";
            bool on = r.Groups[2].Value.ToLowerInvariant() is "켜기" or "켜" or "on";
            return (Prefix + (bt ? "bt:" : "wifi:") + (on ? "on" : "off"), $"{(bt ? "블루투스" : "Wi-Fi")} {(on ? "켜기" : "끄기")}", "무선 장치 조절");
        }
        return null;
    }

    public static void Run(string id)
    {
        var p = id[Prefix.Length..].Split(':');
        switch (p[0])
        {
            case "vol":
                var ep = Endpoint();
                ep.GetMasterVolumeLevelScalar(out float cur);
                float t = p[1][0] is '+' or '-' ? cur + int.Parse(p[1]) / 100f : int.Parse(p[1]) / 100f;
                ep.SetMasterVolumeLevelScalar(Math.Clamp(t, 0f, 1f), Guid.Empty);
                break;
            case "mute": var m = Endpoint(); m.GetMute(out bool muted); m.SetMute(!muted, Guid.Empty); break;
            case "unmute": Endpoint().SetMute(false, Guid.Empty); break;
            case "dark" or "light":
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    int light = p[0] == "light" ? 1 : 0;
                    k.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
                    k.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
                }
                SendMessageTimeout(new IntPtr(0xffff), 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
                break;
            case "bt" or "wifi":
                SetRadio(p[0] == "bt" ? RadioKind.Bluetooth : RadioKind.WiFi, p[1] == "on");
                break;
            default: throw new ArgumentException("Unknown control: " + id);
        }
    }

    static void SetRadio(RadioKind kind, bool on) => Task.Run(async () =>
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
        foreach (var r in await Radio.GetRadiosAsync())
            if (r.Kind == kind) await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
    }).GetAwaiter().GetResult();

    static IAudioEndpointVolume Endpoint()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */);
        var iid = typeof(IAudioEndpointVolume).GUID;
        dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o);
        return (IAudioEndpointVolume)o;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr n);
        void UnregisterControlChangeNotify(IntPtr n);
        void GetChannelCount(out uint c);
        void SetMasterVolumeLevel(float db, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, [In] Guid ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint ch, out float db);
        void GetChannelVolumeLevelScalar(uint ch, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [In] Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
