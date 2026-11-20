using Microsoft.Win32;

namespace Quicklight.Core.Integration;

/// <summary>
/// "Quicklight에게 묻기" in Explorer's right-click menu for files and folders: a classic per-user verb under HKCU, so no
/// administrator rights. Windows 11 shows classic verbs only under "더 보기" (Shift+F10). Selecting several files starts
/// one process per file (<c>--ask "%1"</c>), so the app has to coalesce them into one question.
/// </summary>
public static class ContextMenuRegistration
{
    const string Verb = "Quicklight.Ask";
    const string Title = "Qutra에게 묻기";
    static readonly string[] Targets = [@"Software\Classes\*\shell\" + Verb, @"Software\Classes\Directory\shell\" + Verb];

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Targets[0] + @"\command");
        return key is not null;
    }

    public static void Register(string exePath)
    {
        foreach (var target in Targets)
        {
            using var key = Registry.CurrentUser.CreateSubKey(target);
            key.SetValue(null, Title);
            key.SetValue("Icon", $"\"{exePath}\"");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{exePath}\" --ask \"%1\"");
        }
    }

    public static void Unregister()
    {
        foreach (var target in Targets) Registry.CurrentUser.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
    }
}
