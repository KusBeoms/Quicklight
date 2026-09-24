using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Quicklight.Core;
using Quicklight.Core.Everything;
using Quicklight.Core.Models;
using Quicklight.Core.Providers;
using Quicklight.Core.Shell;

namespace Quicklight;

public partial class MainWindow : Window
{
    const string TopHitGroup = ResultItem.HeroGroup;
    const double ShadowMargin = 24;      // DIPs around the panel, room for the drop shadow (matches Shell.Margin)
    const double MaxPanelHeight = 580;   // DIPs; the bar sits so that a fully expanded panel is centered on screen
    const double ExpandedRadius = 24;    // corner radius with results; the bare search bar is a pill
    const double BackdropPad = 48;       // px captured beyond the panel so the blur has no dark edges

    readonly SearchEngine _engine;
    readonly QuicklightSettings _settings;
    readonly IconLoader _icons = new(64);
    readonly ObservableCollection<ResultItem> _items = [];
    readonly ObservableCollection<ResultItem> _apps = [];
    IReadOnlyList<AppEntry>? _catalogSource;
    CancellationTokenSource? _cts;
    string? _armedKey; // result waiting for the second Enter of a destructive command
    string _lastQuery = "";
    bool _hiding;

    public IntPtr Handle { get; private set; }

    /// <summary>Keep the window open when it loses focus (--pinned, for screenshots and debugging).</summary>
    public bool Pinned { get; set; }

    public MainWindow(SearchEngine engine, QuicklightSettings settings)
    {
        _engine = engine;
        _settings = settings;
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedResults"]).Source = _items;
        ((CollectionViewSource)Resources["GroupedApps"]).Source = _apps;
        Deactivated += (_, _) => { if (!Pinned) HideLauncher(); };
    }

    bool IsGrid => AppGrid.Visibility == Visibility.Visible;
    ListBox ActiveList => IsGrid ? AppGrid : Results;
    ResultItem? Selected => ActiveList.SelectedItem as ResultItem;

    /// <summary>Creates the HWND (needed for the hotkey) without showing the window.</summary>
    public void InitializeHidden()
    {
        Handle = new WindowInteropHelper(this).EnsureHandle();
        ApplyBackdrop();
    }

    /// <summary>"acrylic" = blurred snapshot of what is behind the window; "solid" = opaque panel.</summary>
    public void ApplyBackdrop()
    {
        bool solid = _settings.Backdrop.Equals("solid", StringComparison.OrdinalIgnoreCase);
        Backdrop.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
        Tint.SetResourceReference(Border.BackgroundProperty, solid ? "SolidPanelBrush" : "PanelBrush");
    }

    public void Toggle()
    {
        // IsActive can be stale when a show did not win the foreground, so ask Windows.
        if (IsVisible && !_hiding && NativeUi.IsForeground(Handle)) HideLauncher();
        else ShowLauncher();
    }

    // ---------- show / hide ----------

    public void ShowLauncher()
    {
        var (work, scale) = NativeUi.MonitorUnderCursor();
        int windowWidthPx = (int)(Width * scale);
        int x = work.Left + (work.Width - windowWidthPx) / 2;
        int panelTop = work.Top + Math.Max(0, (int)((work.Height - MaxPanelHeight * scale) / 2));
        int margin = (int)(ShadowMargin * scale);
        int y = panelTop - margin;

        // Snapshot the screen under the panel before the window covers it (skipped when still fading out: it would capture itself).
        if (!IsVisible && Backdrop.Visibility == Visibility.Visible)
            CaptureBackdrop(x + margin, panelTop, (int)((Width - 2 * ShadowMargin) * scale), (int)(MaxPanelHeight * scale), scale);

        NativeUi.Move(Handle, x, y);
        Show();
        Activate();
        if (!NativeUi.ForceForeground(Handle))
        {
            // The very first show can lose the race with layout; retry once the window has rendered.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                if (!IsVisible) return;
                if (!NativeUi.ForceForeground(Handle)) Log.Info("could not take the foreground");
                Activate();
                Keyboard.Focus(Query);
            });
        }
        Query.Focus();
        Keyboard.Focus(Query);
        // Like Spotlight: the previous query stays, selected, so typing replaces it.
        Query.SelectAll();
        AnimateIn();
        _ = UpdateStatusAsync();
        _ = _engine.WarmUpAsync();
    }

    public void HideLauncher()
    {
        if (!IsVisible || _hiding) return;
        _cts?.Cancel(); // no point finishing Everything queries nobody will see
        Disarm();
        if (ActiveList.ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        AnimateOut(() => Hide());
    }

    void CaptureBackdrop(int x, int y, int width, int height, double scale)
    {
        int pad = (int)(BackdropPad * scale);
        try
        {
            Backdrop.Source = ScreenCapture.Capture(x - pad, y - pad, width + 2 * pad, height + 2 * pad, scale);
            Canvas.SetLeft(Backdrop, -pad / scale);
            Canvas.SetTop(Backdrop, -pad / scale);
        }
        catch (Exception ex) { Log.Error("backdrop capture failed", ex); }
    }

    // Fade in while the whole panel comes into focus (blur → sharp) and settles from a slightly smaller scale.
    void AnimateIn()
    {
        _hiding = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(230);
        var blur = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
        Shell.Effect = blur;
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        var focus = new DoubleAnimation(16, 0, duration) { EasingFunction = ease };
        // Drop the effect once sharp: an idle BlurEffect would still cost a render pass per frame.
        focus.Completed += (_, _) => { if (Shell.Effect == blur) Shell.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, focus);
    }

    // The same motion in reverse: out of focus, fade out, then hide.
    void AnimateOut(Action done)
    {
        _hiding = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(160);
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        Shell.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 16, duration) { EasingFunction = ease });
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, duration) { EasingFunction = ease });
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return; // shown again mid-animation
            _hiding = false;
            done();
        };
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- shape ----------

    void Panel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShape();

    /// <summary>A pill while only the search bar shows, rounded corners once the panel expands.</summary>
    void UpdateShape()
    {
        var size = Panel.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        bool expanded = Results.Visibility == Visibility.Visible || IsGrid;
        double r = expanded ? ExpandedRadius : size.Height / 2;
        Panel.Clip = new RectangleGeometry(new Rect(size), r, r);
        ShadowShape.CornerRadius = Outline.CornerRadius = new CornerRadius(r);
    }

    // ---------- searching ----------

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Disarm();
        if (AppCatalog.IsCatalogQuery(Query.Text))
        {
            _cts?.Cancel();
            _lastQuery = Query.Text.Trim();
            ShowCatalog();
            return;
        }
        if (IsGrid) AppGrid.Visibility = Visibility.Collapsed;
        _ = RunSearchAsync(Query.Text);
    }

    async Task RunSearchAsync(string text)
    {
        // Safe to dispose right after Cancel: the previous search only ever checks an already-cancelled token.
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _lastQuery = text.Trim();
        if (_lastQuery.Length == 0)
        {
            Render([], keepSelection: false);
            return;
        }
        try
        {
            // Instant providers first; files slot in as Everything answers (fast name-prefix pass, then the full pass).
            var fast = await _engine.SearchAsync(text, ct: token, files: FileStage.None);
            if (token.IsCancellationRequested) return;
            Render(fast, keepSelection: false);
            // Later passes bring files and translations; skip them only when neither can contribute.
            if (_lastQuery.Length < 2 || (!_settings.FileSearch && !_settings.Translation)) return;

            await Task.Delay(60, token); // debounce the Everything round-trips while typing
            foreach (var stage in new[] { FileStage.Prefix, FileStage.Full })
            {
                var results = await _engine.SearchAsync(text, ct: token, files: stage);
                if (token.IsCancellationRequested) return;
                Render(results, keepSelection: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("search failed", ex); }
    }

    void Render(IReadOnlyList<SearchResult> results, bool keepSelection)
    {
        // Later file stages keep whatever is selected, including the top hit, so Enter opens what the user saw.
        string? selectedKey = keepSelection && Results.SelectedItem is ResultItem sel ? sel.Result.Key : null;

        // Top hit first, then one section per kind in order of each kind's best result.
        var ordered = new List<ResultItem>();
        if (results.Count > 0) ordered.Add(new ResultItem(results[0], TopHitGroup));
        foreach (var group in results.Skip(1).GroupBy(r => ResultItem.KindLabel(r.Kind)))
            ordered.AddRange(group.Select(r => new ResultItem(r, group.Key)));

        _items.Clear();
        int generation = _icons.NextGeneration();
        foreach (var item in ordered)
        {
            _items.Add(item);
            if (item.Result.Key == _armedKey) item.SetSubtitleOverride(ArmedText);
            LoadIcon(item, generation);
        }

        // The armed command dropped out of the list: forget it, so it can never run on a single Enter later.
        if (_armedKey is not null && !ordered.Exists(i => i.Result.Key == _armedKey)) _armedKey = null;

        bool any = _items.Count > 0;
        Separator.Visibility = Results.Visibility = Footer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateShape();
        if (!any) return;
        int index = selectedKey is null ? 0 : Math.Max(0, ordered.FindIndex(i => i.Result.Key == selectedKey));
        Select(index, disarm: false);
    }

    void LoadIcon(ResultItem item, int generation)
    {
        if (item.Icon is not null || item.Result.IconSource is not { } src) return;
        bool isFolder = item.Result.Kind == ResultKind.Folder || item.Result.Kind == ResultKind.Path && src.EndsWith('\\');
        _icons.Load(src, isFolder, generation, img =>
        {
            if (Dispatcher.CheckAccess()) item.Icon = img;
            else Dispatcher.BeginInvoke(() => item.Icon = img);
        });
    }

    // ---------- all apps grid ----------

    void ShowCatalog()
    {
        var source = _engine.Apps.All;
        if (!ReferenceEquals(source, _catalogSource))
        {
            // Build once per app index; the tiles keep their icons between openings.
            _catalogSource = source;
            _apps.Clear();
            foreach (var section in AppCatalog.Build(source))
                foreach (var app in section.Apps)
                    _apps.Add(new ResultItem(new SearchResult
                    {
                        Title = app.Name,
                        Subtitle = app.FilePath ?? "앱",
                        Kind = ResultKind.App,
                        Target = app.LaunchTarget,
                        IconSource = app.LaunchTarget,
                        RevealPath = app.FilePath,
                    }, section.Name));
        }
        int generation = _icons.NextGeneration();
        foreach (var item in _apps) LoadIcon(item, generation);

        _items.Clear();
        Results.Visibility = Visibility.Collapsed;
        AppGrid.Visibility = Separator.Visibility = Footer.Visibility = Visibility.Visible;
        UpdateShape();
        if (_apps.Count == 0) { FooterText.Text = "앱 목록을 읽는 중입니다…"; return; }
        AppGrid.SelectedIndex = 0;
        AppGrid.ScrollIntoView(AppGrid.SelectedItem);
        UpdateFooter();
    }

    /// <summary>Up/Down in the grid: the nearest tile in the next row above or below, across section breaks.</summary>
    void MoveInGrid(int direction)
    {
        if (AppGrid.ItemContainerGenerator.ContainerFromIndex(AppGrid.SelectedIndex) is not ListBoxItem current) return;
        var here = current.TranslatePoint(new Point(0, 0), AppGrid);
        var candidates = new List<(int Index, Point At)>();
        for (int i = 0; i < _apps.Count; i++)
            if (AppGrid.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c)
                candidates.Add((i, c.TranslatePoint(new Point(0, 0), AppGrid)));

        var row = candidates.Where(c => direction > 0 ? c.At.Y > here.Y + 10 : c.At.Y < here.Y - 10).ToList();
        if (row.Count == 0) return;
        double rowY = direction > 0 ? row.Min(c => c.At.Y) : row.Max(c => c.At.Y);
        var target = row.Where(c => Math.Abs(c.At.Y - rowY) < 10).MinBy(c => Math.Abs(c.At.X - here.X));
        SelectIn(AppGrid, target.Index);
    }

    // ---------- keyboard and mouse ----------

    void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Down when IsGrid: MoveInGrid(+1); e.Handled = true; break;
            case Key.Up when IsGrid: MoveInGrid(-1); e.Handled = true; break;
            case Key.Right when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex + 1); e.Handled = true; break;
            case Key.Left when IsGrid: SelectIn(AppGrid, AppGrid.SelectedIndex - 1); e.Handled = true; break;
            case Key.Down: Select(Results.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: Select(Results.SelectedIndex - 1); e.Handled = true; break;
            case Key.PageDown: SelectIn(ActiveList, ActiveList.SelectedIndex + (IsGrid ? 21 : 5)); e.Handled = true; break;
            case Key.PageUp: SelectIn(ActiveList, ActiveList.SelectedIndex - (IsGrid ? 21 : 5)); e.Handled = true; break;
            case Key.Enter when mods.HasFlag(ModifierKeys.Control):
                if (Selected is { } toReveal) Reveal(toReveal);
                e.Handled = true; break;
            case Key.Enter:
                if (Selected is { } item) Execute(item);
                e.Handled = true; break;
            case Key.Escape:
                if (_armedKey is not null) Disarm();
                else if (Query.Text.Length > 0) Query.Clear();
                else HideLauncher();
                e.Handled = true; break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                if (Selected is { } toCopy) CopyPath(toCopy);
                e.Handled = true; break;
        }
    }

    static (ListBoxItem? Container, ResultItem? Item) HitItem(ListBox list, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) as ListBoxItem;
        return (container, container?.DataContext as ResultItem);
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep keyboard focus in the search box; clicking selects, double-clicking opens.
        var list = (ListBox)sender;
        if (HitItem(list, e).Item is not { } item) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        if (e.ClickCount >= 2) Execute(item);
        Query.Focus();
    }

    void List_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var (container, item) = HitItem(list, e);
        if (container is null || item is null) return;
        e.Handled = true;
        list.SelectedItem = item;
        UpdateFooter();
        var menu = BuildMenu(item);
        menu.PlacementTarget = container;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        list.ContextMenu = menu;
        menu.Closed += (_, _) => Query.Focus();
        menu.IsOpen = true;
    }

    ContextMenu BuildMenu(ResultItem item)
    {
        var r = item.Result;
        var menu = new ContextMenu { Style = (Style)FindResource(typeof(ContextMenu)) };
        void Add(string glyph, string header, Action action)
        {
            var mi = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 },
                Style = (Style)FindResource(typeof(MenuItem)),
            };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Add("", r.Action == ActionType.Copy ? "복사" : "열기", () => Execute(item));
        if (ShellLauncher.CanRunAsAdmin(r.RevealPath ?? r.Target))
            Add("", "관리자 권한으로 실행", () => RunAsAdmin(item, r.RevealPath ?? r.Target));
        if (r.RevealPath is not null)
        {
            Add("", "파일 탐색기에서 열기", () => Reveal(item));
            Add("", "경로 복사", () => CopyPath(item));
        }
        return menu;
    }

    void Select(int index, bool disarm = true) => SelectIn(Results, index, disarm);

    void SelectIn(ListBox list, int index, bool disarm = true)
    {
        if (list.Items.Count == 0) return;
        index = Math.Clamp(index, 0, list.Items.Count - 1);
        if (disarm && list.SelectedIndex != index) Disarm();
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
        UpdateFooter();
    }

    // ---------- actions ----------

    void Execute(ResultItem item)
    {
        var r = item.Result;
        if (r.Action == ActionType.None) return; // informational row ("translating…")
        if (r.RequiresConfirmation && _armedKey != r.Key)
        {
            Disarm();
            _armedKey = r.Key;
            item.SetSubtitleOverride(ArmedText);
            return;
        }

        if (r.Action == ActionType.Copy)
        {
            TrySetClipboard(r.Target);
            HideLauncher();
            return;
        }

        HideLauncher();
        _engine.RecordSelection(_lastQuery, r);
        try { SearchEngine.Execute(r); }
        catch (Exception ex) { ShowError("실행하지 못했습니다", r.Target, ex); }
    }

    void RunAsAdmin(ResultItem item, string path)
    {
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
        try { ShellLauncher.RunAsAdmin(path); }
        catch (Exception ex) { ShowError("관리자 권한으로 실행하지 못했습니다", path, ex); }
    }

    void Reveal(ResultItem item)
    {
        if (item.Result.RevealPath is not { } path) return;
        HideLauncher();
        _engine.RecordSelection(_lastQuery, item.Result);
        try { ShellLauncher.Reveal(path); }
        catch (Exception ex) { Log.Error("reveal " + path, ex); }
    }

    void ShowError(string message, string target, Exception ex)
    {
        Log.Error($"{message}: {target}", ex);
        ShowLauncher();
        Status.Text = $"{message}: {ex.Message}";
    }

    void CopyPath(ResultItem item)
    {
        if (TrySetClipboard(item.Result.RevealPath ?? item.Result.Target)) Status.Text = "경로를 복사했습니다";
    }

    static bool TrySetClipboard(string text)
    {
        // The clipboard can be briefly locked by another process.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
        return false;
    }

    const string ArmedText = "한 번 더 Enter를 누르면 실행합니다 · Esc로 취소";

    void Disarm()
    {
        if (_armedKey is null) return;
        foreach (var i in _items) if (i.Result.Key == _armedKey) i.SetSubtitleOverride(null);
        _armedKey = null;
    }

    void UpdateFooter()
    {
        if (Selected is not { } item) { FooterText.Text = ""; return; }
        var r = item.Result;
        if (IsGrid)
        {
            FooterText.Text = $"앱 {_apps.Count}개      ↵  실행      ←↑↓→  이동      우클릭  더 보기      Esc  돌아가기";
            return;
        }
        if (r.Action == ActionType.None) { FooterText.Text = "Esc  닫기"; return; }
        string enter = r.Action switch
        {
            ActionType.Copy => "결과 복사",
            ActionType.System => "실행",
            _ => r.Kind switch
            {
                ResultKind.App => "실행",
                ResultKind.Url or ResultKind.WebSearch => "브라우저에서 열기",
                _ => "열기",
            },
        };
        var parts = new List<string> { $"↵  {enter}" };
        if (r.RevealPath is not null) parts.Add("Ctrl+↵  폴더에서 보기");
        if (r.RevealPath is not null || r.Kind is ResultKind.Url) parts.Add("Ctrl+Shift+C  경로 복사");
        if (r.RevealPath is not null) parts.Add("우클릭  더 보기");
        parts.Add("Esc  닫기");
        FooterText.Text = string.Join("      ", parts);
    }

    /// <summary>Checks Everything off the UI thread: a busy Everything must not delay the first paint.</summary>
    async Task UpdateStatusAsync()
    {
        const string noEverything = "Everything 응답 없음 · 파일 결과 제외";
        if (Status.Text == noEverything) Status.Text = "";
        if (!_settings.FileSearch) return;
        var version = await Task.Run(() => EverythingClient.GetVersion(300));
        // Only touch our own message, so an error shown meanwhile (e.g. a failed launch) stays visible.
        if (version is null && Status.Text.Length == 0) Status.Text = noEverything;
        else if (version is not null && Status.Text == noEverything) Status.Text = "";
    }
}

static class ListExtensions
{
    public static int FindIndex<T>(this List<T> list, Func<T, bool> pred) => list.FindIndex(new Predicate<T>(pred));
}
