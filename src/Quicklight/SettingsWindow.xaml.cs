using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Quicklight.Core;
using Quicklight.Core.Ai;

namespace Quicklight;

/// <summary>Settings as a window (Ctrl+, in the launcher, or the tray menu). Saving writes settings.json and applies it.</summary>
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
        Closed += (_, _) => { _open = null; _aiCts?.Cancel(); _controlCts?.Cancel(); _pdfCts?.Cancel(); };
        RefreshAi();
        RefreshControl();
        RefreshPdf();
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
        (s.AiKeepConversations switch { "1y" => KeepYear, "forever" => KeepForever, _ => KeepMonth }).IsChecked = true;
        (s.AiOpenWith == "new" ? OpenNew : OpenRecent).IsChecked = true;
        ExplorerMenu.IsChecked = s.ExplorerMenu;
        AiEnabled.IsChecked = s.AiEnabled;
        ComputerControl.IsChecked = s.ComputerControl;
        HangulHotkey.IsChecked = s.HangulHotkey;
        (s.Theme switch { "dark" => ThemeDark, "light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        (s.Backdrop == "solid" ? BackdropSolid : BackdropAcrylic).IsChecked = true;
        LaunchAtStartup.IsChecked = s.LaunchAtStartup;
        AutoUpdate.IsChecked = s.AutoUpdate;
        Terminal.IsChecked = s.Terminal;
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
        if (MessageBox.Show(this, "기본값으로 되돌릴까요?\n(스니펫, 사용자 명령 제외)", "Quicklight",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Fill(new QuicklightSettings());
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings;
        s.Hotkey = HotkeyBox.Text;
        s.AiKeepConversations = KeepYear.IsChecked == true ? "1y" : KeepForever.IsChecked == true ? "forever" : "30d";
        s.AiOpenWith = OpenNew.IsChecked == true ? "new" : "recent";
        s.ExplorerMenu = ExplorerMenu.IsChecked == true;
        s.AiEnabled = AiEnabled.IsChecked == true;
        s.ComputerControl = ComputerControl.IsChecked == true;
        s.HangulHotkey = HangulHotkey.IsChecked == true;
        s.Theme = ThemeDark.IsChecked == true ? "dark" : ThemeLight.IsChecked == true ? "light" : "system";
        s.Backdrop = BackdropSolid.IsChecked == true ? "solid" : "acrylic";
        s.LaunchAtStartup = LaunchAtStartup.IsChecked == true;
        s.AutoUpdate = AutoUpdate.IsChecked == true;
        s.Terminal = Terminal.IsChecked == true;
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
            MessageBox.Show(this, "저장 실패\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    // ---------- AI ----------

    CancellationTokenSource? _aiCts; // a download is running

    void RefreshAi()
    {
        bool ram = AiModel.HasEnoughRam, installed = AiModel.IsInstalled, busy = _aiCts is not null;
        AiStatus.Text = !ram ? $"RAM {AiModel.RamBytes / (1L << 30)}GB: 64GB 이상 필요"
            : installed ? (AiModel.HasVision ? "설치됨" : "설치됨 · 이미지 파일 없음") : "미설치";
        AiDownload.Visibility = ram && (!installed || !AiModel.HasVision) ? Visibility.Visible : Visibility.Collapsed;
        AiDownload.Content = busy ? "취소" : "다운로드";
        AiDelete.Visibility = installed && !busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- computer control (agent-desktop) ----------

    CancellationTokenSource? _controlCts;

    void RefreshControl()
    {
        bool busy = _controlCts is not null, installed = AgentDesktop.IsInstalled;
        ControlStatus.Text = installed ? (AgentDesktop.CheckAvailable() ?? $"설치됨 · {AgentDesktop.Version}") : "미설치";
        ControlDownload.Visibility = installed && !busy ? Visibility.Collapsed : Visibility.Visible;
        ControlDownload.Content = busy ? "취소" : "다운로드";
        ControlDelete.Visibility = installed && !busy ? Visibility.Visible : Visibility.Collapsed;
        ComputerControl.IsEnabled = installed;
    }

    async void ControlDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_controlCts is { } running) { running.Cancel(); return; }
        var cts = _controlCts = new CancellationTokenSource();
        ControlProgress.Visibility = Visibility.Visible;
        ControlProgress.IsIndeterminate = true;
        RefreshControl();
        var progress = new Progress<(string Label, double? Fraction)>(p =>
        {
            ControlStatus.Text = p.Label;
            ControlProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } f) ControlProgress.Value = f;
        });
        try
        {
            await AgentDesktop.InstallAsync(progress, cts.Token);
            ComputerControl.IsChecked = true; // downloaded on purpose: switch it on (Save applies it)
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("agent-desktop download failed", ex);
            MessageBox.Show(this, "내려받기 실패\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _controlCts = null;
            ControlProgress.Visibility = Visibility.Collapsed;
            RefreshControl();
        }
    }

    void ControlDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "컴퓨터 제어를 삭제할까요?", "Quicklight", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { AgentDesktop.Delete(); ComputerControl.IsChecked = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("agent-desktop delete failed", ex);
            MessageBox.Show(this, "삭제 실패\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshControl();
    }

    // ---------- PDF reading (PdfPig) ----------

    CancellationTokenSource? _pdfCts;

    void RefreshPdf()
    {
        bool busy = _pdfCts is not null, installed = PdfText.IsInstalled;
        PdfStatus.Text = installed ? $"설치됨 · {PdfText.Version}" : "미설치";
        PdfDownload.Visibility = installed && !busy ? Visibility.Collapsed : Visibility.Visible;
        PdfDownload.Content = busy ? "취소" : "다운로드";
        PdfDelete.Visibility = installed && !busy ? Visibility.Visible : Visibility.Collapsed;
    }

    async void PdfDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfCts is { } running) { running.Cancel(); return; }
        var cts = _pdfCts = new CancellationTokenSource();
        PdfProgress.Visibility = Visibility.Visible;
        PdfProgress.IsIndeterminate = true;
        RefreshPdf();
        var progress = new Progress<(string Label, double? Fraction)>(p =>
        {
            PdfStatus.Text = p.Label;
            PdfProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } f) PdfProgress.Value = f;
        });
        try { await PdfText.InstallAsync(progress, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("pdf download failed", ex);
            MessageBox.Show(this, "내려받기 실패\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _pdfCts = null;
            PdfProgress.Visibility = Visibility.Collapsed;
            RefreshPdf();
        }
    }

    void PdfDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "PDF 읽기를 삭제할까요?", "Quicklight", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { PdfText.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("pdf delete failed", ex);
            MessageBox.Show(this, "삭제 실패\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshPdf();
    }

    async void AiDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_aiCts is { } running) { running.Cancel(); return; }
        var cts = _aiCts = new CancellationTokenSource();
        AiProgress.Visibility = Visibility.Visible;
        AiProgress.IsIndeterminate = true;
        RefreshAi();
        var progress = new Progress<(string Label, double? Fraction)>(p =>
        {
            AiStatus.Text = p.Label;
            AiProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } f) AiProgress.Value = f;
        });
        try { await AiModel.InstallAsync(progress, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("ai download failed", ex);
            MessageBox.Show(this, "내려받기 실패 (다시 시도하면 이어받음)\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _aiCts = null;
            AiProgress.Visibility = Visibility.Collapsed;
            RefreshAi();
        }
    }

    void AiDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "모델을 삭제할까요?", "Quicklight", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try { Siri.AiHost.Current?.Session.Stop(); AiModel.Delete(); } // an idle server would lock the files
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("ai delete failed", ex);
            MessageBox.Show(this, "삭제 실패: 사용 중입니다. 잠시 뒤 다시 시도하세요.\n" + ex.Message, "Quicklight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshAi();
        RefreshControl(); // the model's folder holds computer control and PDF reading too
        RefreshPdf();
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
        var box = (TextBox)sender;
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
        if (HotkeyManager.TryParse(spec, out _, out _)) box.Text = spec;
    }
}
