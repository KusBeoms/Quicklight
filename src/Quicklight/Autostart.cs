using Microsoft.Win32;
using Quicklight.Core;

namespace Quicklight;

/// <summary>HKCU Run entry, so Quicklight starts with Windows like PowerToys Run did.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Quicklight";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Sync(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else if (k.GetValue(ValueName) is not null) k.DeleteValue(ValueName);
        }
        catch (Exception ex) { Log.Error("autostart update failed", ex); }
    }
}
