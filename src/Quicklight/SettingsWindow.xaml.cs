using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Quicklight.Core;

namespace Quicklight;

/// <summary>Settings as a window (Alt+. in the launcher, or the tray menu). Saving writes settings.json and applies it.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>One editable row of a keyword, snippet or command list.</summary>
    public sealed class Row
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string C { get; set; } = "";
        public string D { get; set; } = "";
    }

    static SettingsWindow? _open;

    readonly QuicklightSettings _settings;
    readonly Action _apply;
    ObservableCollection<Row> _engines = [], _snippets = [], _commands = [];

    public static void Open(QuicklightSettings settings, Action apply)
    {
        _open ??= new SettingsWindow(settings, apply);
        _open.Show();
        if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    SettingsWindow(QuicklightSettings s, Action apply)
    {
        _settings = s;
        _apply = apply;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeUi.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
        Closed += (_, _) => _open = null;
        Fill(s);
        Snippets.ItemsSource = _snippets = new(s.Snippets.Select(p => new Row { A = p.Key, B = p.Value }));
        Commands.ItemsSource = _commands = new(s.Commands.Select(c => new Row
        {
            A = c.Name, B = c.Path, C = c.Arguments ?? "", D = string.Join(", ", c.Aliases ?? []),
        }));
    }

    /// <summary>Puts <paramref name="s"/> into the form, all but the snippets and commands (the user's own content).</summary>
    void Fill(QuicklightSettings s)
    {
        HotkeyBox.Text = s.Hotkey;
        (s.Theme switch { "dark" => ThemeDark, "light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        (s.Backdrop == "solid" ? BackdropSolid : BackdropAcrylic).IsChecked = true;
        LaunchAtStartup.IsChecked = s.LaunchAtStartup;
        AutoUpdate.IsChecked = s.AutoUpdate;
        FileSearch.IsChecked = s.FileSearch;
        AdvancedSearch.IsChecked = s.AdvancedSearch;
        MaxResults.Text = s.MaxResults.ToString();
        MaxFileResults.Text = s.MaxFileResults.ToString();
        MaxFolderResults.Text = s.MaxFolderResults.ToString();
        DemotedPaths.Text = string.Join(Environment.NewLine, s.DemotedPaths);
        ExcludedPaths.Text = string.Join(Environment.NewLine, s.ExcludedPaths);
        WebSearchUrl.Text = s.WebSearchUrl;
        CurrencyConversion.IsChecked = s.CurrencyConversion;
        DefaultCurrency.Text = s.DefaultCurrency;

        SearchEngines.ItemsSource = _engines = new(s.SearchEngines.Select(p => new Row { A = p.Key, B = p.Value }));
    }

    /// <summary>Refills the form with the defaults; nothing changes until Save.</summary>
    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "스니펫과 사용자 명령을 뺀 모든 설정을 기본값으로 되돌릴까요?\n저장을 눌러야 적용됩니다.", "Quicklight",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Fill(new QuicklightSettings());
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings;
        s.Hotkey = HotkeyBox.Text;
        s.Theme = ThemeDark.IsChecked == true ? "dark" : ThemeLight.IsChecked == true ? "light" : "system";
        s.Backdrop = BackdropSolid.IsChecked == true ? "solid" : "acrylic";
        s.LaunchAtStartup = LaunchAtStartup.IsChecked == true;
        s.AutoUpdate = AutoUpdate.IsChecked == true;
        s.FileSearch = FileSearch.IsChecked == true;
        s.AdvancedSearch = AdvancedSearch.IsChecked == true;
        s.MaxResults = Count(MaxResults.Text, s.MaxResults);
        s.MaxFileResults = Count(MaxFileResults.Text, s.MaxFileResults);
        s.MaxFolderResults = Count(MaxFolderResults.Text, s.MaxFolderResults);
        s.DemotedPaths = Lines(DemotedPaths.Text);
        s.ExcludedPaths = Lines(ExcludedPaths.Text);
        if (WebSearchUrl.Text.Trim() is { Length: > 0 } url) s.WebSearchUrl = url;
        s.CurrencyConversion = CurrencyConversion.IsChecked == true;
        s.DefaultCurrency = DefaultCurrency.Text.Trim() is { Length: > 0 } currency ? currency : "auto";
        s.SearchEngines = Pairs(_engines, trimValue: true);
        s.Snippets = Pairs(_snippets, trimValue: false);
        s.Commands = _commands.Where(r => r.A.Trim().Length > 0 && r.B.Trim().Length > 0)
            .Select(r => new CustomCommand(r.A.Trim(), r.B.Trim(),
                r.C.Trim() is { Length: > 0 } args ? args : null,
                Lines(r.D.Replace(',', '\n')) is { Count: > 0 } aliases ? [.. aliases] : null))
            .ToList();

        try { s.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings save failed", ex);
            MessageBox.Show(this, "설정을 저장하지 못했습니다.\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _apply();
        Close();
    }

    static int Count(string text, int fallback) => int.TryParse(text.Trim(), out var n) && n >= 0 ? n : fallback;

    static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    static Dictionary<string, string> Pairs(IEnumerable<Row> rows, bool trimValue)
    {
        var map = new Dictionary<string, string>();
        foreach (var r in rows)
            if (r.A.Trim() is { Length: > 0 } key && r.B.Trim().Length > 0)
                map[key] = trimValue ? r.B.Trim() : r.B.Replace("\r\n", "\n");
        return map;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(QuicklightSettings.DefaultPath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("open settings file", ex); }
    }

    void AddEngine_Click(object sender, RoutedEventArgs e) => _engines.Add(new Row { B = "https://www.google.com/search?q={0}" });
    void AddSnippet_Click(object sender, RoutedEventArgs e) => _snippets.Add(new Row());
    void AddCommand_Click(object sender, RoutedEventArgs e) => _commands.Add(new Row());

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Row row) return;
        _engines.Remove(row);
        _snippets.Remove(row);
        _commands.Remove(row);
    }

    /// <summary>Records the pressed combination, in the format HotkeyManager parses.</summary>
    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && key is Key.Tab or Key.Escape) return; // leave the box, close the window
        e.Handled = true;
        string? name = key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Oem3 => "`",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            _ => null,
        };
        if (name is null) return;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);
        var spec = string.Join("+", parts);
        if (HotkeyManager.TryParse(spec, out _, out _)) HotkeyBox.Text = spec;
    }
}
