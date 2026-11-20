using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Quicklight.Core.Ai;

namespace Quicklight.Siri;

/// <summary>
/// The Siri-style app: past conversations on the left (cards or a list, pinned first), the chosen one on the right with
/// its input bar. Row menu: pin, rename, delete; Ctrl-click selects several to delete at once.
/// </summary>
public partial class SiriAppWindow : Window
{
    readonly AiHost _host;
    readonly HashSet<string> _selected = [];
    string? _currentId;
    bool _grid = true;
    string _filter = "";

    public SiriAppWindow(AiHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeUi.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
        Chat.Attach(host);
        Chat.ConversationStarted += c => { _currentId = c.Id; _selected.Clear(); _selected.Add(c.Id); };
        host.Store.Changed += Store_Changed;
        Closed += (_, _) => { host.Store.Changed -= Store_Changed; host.ViewClosed(); };
        Loaded += (_, _) => Rebuild();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _host.IsBusy) { _host.Cancel(); e.Handled = true; }
        };
    }

    public void Open(Conversation? conv)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Select(conv, extend: false);
        Chat.FocusInput();
    }

    // ---------- history ----------

    void Store_Changed() => Dispatcher.BeginInvoke(() => { if (IsVisible) Rebuild(); });

    void Rebuild()
    {
        History.Children.Clear();
        IEnumerable<Conversation> all = _host.Store.All;
        if (_filter.Length > 0)
            all = all.Where(c => c.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) ||
                                 _host.Store.MessagesOf(c).Any(m => m.Text.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)));
        foreach (var c in all.ToList()) History.Children.Add(Card(c));
        var title = Chat.Conversation is { } cur && cur.Title.Length > 0 ? cur.Title : "새로운 대화";
        if (TitleText.Text == title) return;
        TitleText.Text = title;
        if (TitleText.IsVisible) UiMotion.Enter(TitleText, offset: 2, duration: UiMotion.Quick);
    }

    UIElement Card(Conversation c)
    {
        bool selected = _selected.Contains(c.Id);
        var stack = new StackPanel();
        var head = new DockPanel();
        if (c.Pinned)
        {
            var pin = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Margin = new Thickness(6, 3, 0, 0) };
            pin.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            DockPanel.SetDock(pin, Dock.Right);
            head.Children.Add(pin);
        }
        var title = new TextBlock
        {
            Text = c.Title.Length > 0 ? c.Title : "새로운 대화", FontWeight = FontWeights.SemiBold, FontSize = 13.5,
            TextWrapping = _grid ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = _grid ? 40 : 20,
        };
        head.Children.Add(title);
        stack.Children.Add(head);
        var snippet = _host.Store.MessagesOf(c).LastOrDefault(m => !m.IsError)?.Text.ReplaceLineEndings(" ") ?? "";
        var sub = new TextBlock
        {
            Text = _grid ? snippet : When(c.Updated) + (snippet.Length > 0 ? "  " + snippet : ""), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = _grid ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = _grid ? 45 : 16,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        stack.Children.Add(sub);
        if (_grid)
        {
            var when = new TextBlock { Text = When(c.Updated), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            when.SetResourceReference(TextBlock.ForegroundProperty, "PlaceholderBrush");
            stack.Children.Add(when);
        }

        var card = new Border
        {
            Width = _grid ? 120 : 252, Height = _grid ? 132 : 56, Margin = new Thickness(_grid ? 4 : 0, 6, _grid ? 4 : 0, 0), Padding = new Thickness(11, _grid ? 11 : 8, 11, 8),
            CornerRadius = new CornerRadius(_grid ? 16 : 12), Cursor = Cursors.Hand, Child = stack, Tag = c, ClipToBounds = true,
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "GlyphBackBrush" : "SolidPanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "SeparatorBrush");
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(c, extend: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        };
        card.ContextMenu = RowMenu(c);
        card.ContextMenuOpening += (_, _) =>
        {
            if (!_selected.Contains(c.Id)) Select(c, extend: false); // the menu acts on what was right-clicked
            card.ContextMenu = RowMenu(c);
        };
        return card;
    }

    static string When(DateTime t)
    {
        var d = DateTime.Now.Date - t.Date;
        return d.Days == 0 ? t.ToString("HH:mm") : d.Days == 1 ? "어제" : t.Year == DateTime.Now.Year ? t.ToString("M월 d일") : t.ToString("yyyy.M.d");
    }

    ContextMenu RowMenu(Conversation c)
    {
        var menu = new ContextMenu();
        if (_selected.Count > 1)
        {
            var del = new MenuItem { Header = $"선택한 대화 삭제 ({_selected.Count})" };
            del.Click += (_, _) => DeleteSelected();
            menu.Items.Add(del);
            return menu;
        }
        var pin = new MenuItem { Header = c.Pinned ? "고정 해제" : "고정" };
        pin.Click += (_, _) => _host.Store.SetPinned(c.Id, !c.Pinned);
        var rename = new MenuItem { Header = "이름 변경…" };
        rename.Click += (_, _) =>
        {
            if (PromptWindow.Ask(this, "이름 변경", c.Title) is { } name && name.Trim().Length > 0) _host.Store.Rename(c.Id, name.Trim());
        };
        var delete = new MenuItem { Header = "삭제…" };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(pin);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    void DeleteSelected()
    {
        var ids = _selected.ToList();
        if (ids.Count == 0) return;
        var what = ids.Count == 1 ? "이 대화를" : $"선택한 대화 {ids.Count}개를";
        if (MessageBox.Show(this, $"{what} 삭제할까요?", "Quicklight", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return; // Enter must not delete
        if (_host.BusyConversation is { } busy && ids.Contains(busy.Id)) _host.Cancel(); // deleting what is being answered
        _host.Store.Delete(ids);
        _selected.Clear();
        if (_currentId is not null && ids.Contains(_currentId)) { _currentId = null; Chat.Show(null); }
        Rebuild();
    }

    void Select(Conversation? c, bool extend)
    {
        if (c is null) { _selected.Clear(); _currentId = null; Chat.Show(null); Rebuild(); return; }
        if (extend)
        {
            if (!_selected.Remove(c.Id)) _selected.Add(c.Id);
        }
        else { _selected.Clear(); _selected.Add(c.Id); }
        _currentId = c.Id;
        Chat.Show(c);
        Rebuild();
    }

    // ---------- toolbar ----------

    void New_Click(object sender, RoutedEventArgs e)
    {
        Select(null, extend: false);
        Chat.FocusInput();
    }

    void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Visibility = SearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SearchBox.Visibility == Visibility.Visible)
        {
            UiMotion.Enter(SearchBox, offset: 3, duration: UiMotion.Quick);
            SearchBox.Focus();
        }
        else { SearchBox.Clear(); }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        Rebuild();
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        GridItem.IsChecked = _grid;
        ListItem.IsChecked = !_grid;
        OptionsMenu.PlacementTarget = OptionsButton;
        OptionsMenu.IsOpen = true;
    }

    void View_Click(object sender, RoutedEventArgs e)
    {
        _grid = sender == GridItem;
        Rebuild();
    }
}

/// <summary>A one-line text prompt (rename), in the system theme.</summary>
sealed class PromptWindow : Window
{
    readonly TextBox _box = new() { Padding = new Thickness(8, 6, 8, 6), MinWidth = 280 };

    PromptWindow(string title, string value)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidPanelBrush");
        _box.Text = value;
        _box.SelectAll();
        var ok = new Button { Content = "확인", IsDefault = true, MinWidth = 72, Padding = new Thickness(0, 5, 0, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 72, Padding = new Thickness(0, 5, 0, 5) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _box.Focus();
        SourceInitialized += (_, _) => NativeUi.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
    }

    public static string? Ask(Window owner, string title, string value)
    {
        var w = new PromptWindow(title, value) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}
