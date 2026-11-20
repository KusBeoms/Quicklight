using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Quicklight.Core;
using Quicklight.Core.Ai;

namespace Quicklight.Siri;

public partial class ConversationView : UserControl
{
    AiHost? _host;
    Conversation? _conv;
    readonly List<Attachment> _attachments = [];
    bool _stick = true; // follow new text; off while the reader has scrolled up
    bool _scrollQueued;
    AiProgress? _progress;
    FrameworkElement? _consentCard;
    bool _wasBusy;
    string? _confirmation;
    readonly VoiceInput _voice = new();

    public ConversationView()
    {
        InitializeComponent();
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddAttachments(files);
        };
        Loaded += (_, _) => { if (_host is not null) _host.Changed += OnHostChanged; };
        Unloaded += (_, _) =>
        {
            if (_host is not null) _host.Changed -= OnHostChanged;
            _voice.Stop();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(animate: false); };
        _voice.Changed += () => Dispatcher.BeginInvoke(OnListeningChanged);
        UpdateInput();
    }

    public Conversation? Conversation => _conv;

    /// <summary>A conversation was started by sending the first message from an empty view.</summary>
    public event Action<Conversation>? ConversationStarted;

    public void Attach(AiHost host) => _host = host;

    void OnHostChanged()
    {
        if (IsVisible) Render(animate: true);
    }

    /// <summary>Shows <paramref name="conv"/>, or an empty new conversation when null.</summary>
    public void Show(Conversation? conv)
    {
        if (ReferenceEquals(_conv, conv)) return;
        _conv = conv;
        _stick = true;
        Messages.Children.Clear();
        Activity.Children.Clear();
        _progress = null;
        _consentCard = null;
        _confirmation = null;
        _wasBusy = false;
        Render(animate: false);
        if (IsVisible && conv is not null) UiMotion.Enter(Messages, offset: 4);
    }

    public void FocusInput()
    {
        Input.Focus();
        Keyboard.Focus(Input);
        Input.CaretIndex = Input.Text.Length; // the cursor after whatever is already typed
    }

    /// <summary>Puts text in the input box (the selected text a question starts from) and leaves the cursor after it.</summary>
    public void SetDraft(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    // ---------- drawing ----------

    void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0) _stick = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 8;
    }

    void Messages_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        foreach (var bubble in Messages.Children.OfType<Border>()) bubble.MaxWidth = BubbleWidth;
    }

    double BubbleWidth => Math.Max(140, Messages.ActualWidth * 0.8);

    void Render(bool animate)
    {
        bool changed = false;
        if (_conv is { } conv && _host is { } host)
        {
            // Keep the existing elements and their layout across state changes.
            var messages = host.Store.MessagesOf(conv);
            for (int i = Messages.Children.Count; i < messages.Count; i++)
            {
                var message = messages[i];
                var element = message.Role == ChatRole.User ? UserBubble(message) : AssistantBlock(message);
                if (animate) UiMotion.Enter(element);
                Messages.Children.Add(message.Role == ChatRole.Assistant && animate ? ReplaceProgress(element) : element);
                changed = true;
            }

            bool busy = ReferenceEquals(host.BusyConversation, conv) && messages.LastOrDefault()?.Role == ChatRole.User;
            var confirmation = busy ? host.PendingConfirmation : null;
            changed |= busy != _wasBusy || confirmation != _confirmation;
            UpdateActivity(host, busy, confirmation, animate);
        }
        UpdateInput();
        if (_stick && !_scrollQueued && (changed || !animate))
        {
            _scrollQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _scrollQueued = false;
                if (_stick && IsVisible) Scroll.ScrollToEnd();
            });
        }
    }

    UIElement ReplaceProgress(UIElement answer)
    {
        if (_progress is not { } progress || !UiMotion.Enabled) return answer;
        Activity.Children.Remove(progress);
        _progress = null;
        // Overlay the departing status on the reply so the reply starts at the same spot.
        var slot = new Grid();
        slot.Children.Add(answer);
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(progress);
        slot.Children.Add(overlay);
        UiMotion.Fade(progress, 0, UiMotion.Quick, () => slot.Children.Remove(overlay));
        return slot;
    }

    UIElement UserBubble(ChatMessage m)
    {
        // The question: a small rounded bubble on the right.
        var stack = new StackPanel();
        var text = new TextBlock { Text = m.Text, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        stack.Children.Add(text);
        foreach (var a in m.Attachments)
        {
            var name = new TextBlock { Text = "📎 " + (a.Name ?? Path.GetFileName(a.Path)), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            stack.Children.Add(name);
        }
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 8, 12, 9), HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = BubbleWidth, Margin = new Thickness(0, 0, 0, 14), Child = stack,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
        return bubble;
    }

    UIElement AssistantBlock(ChatMessage m)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (m.IsError)
        {
            var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12) };
            card.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
            var inner = new StackPanel();
            var msg = new TextBlock { Text = m.Text, FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            inner.Children.Add(msg);
            var retry = new Button { Content = "다시 시도", Style = (Style)FindResource("SiriFlatButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            retry.Click += (_, _) => Retry();
            inner.Children.Add(retry);
            card.Child = inner;
            stack.Children.Add(card);
            return stack;
        }
        stack.Children.Add(MarkdownView.Build(m.Text, this));
        var copy = new Button
        {
            Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, ToolTip = "복사", Cursor = Cursors.Hand, Focusable = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0.55,
        };
        copy.SetResourceReference(ForegroundProperty, "SubtleTextBrush");
        copy.MouseEnter += (_, _) => UiMotion.Fade(copy, 1, UiMotion.Quick);
        copy.MouseLeave += (_, _) => UiMotion.Fade(copy, 0.55, UiMotion.Quick);
        copy.Click += (_, _) => { try { Clipboard.SetText(m.Text); } catch (System.Runtime.InteropServices.COMException) { } };
        stack.Children.Add(copy);
        return stack;
    }

    void UpdateActivity(AiHost host, bool busy, string? confirmation, bool animate)
    {
        bool started = busy && !_wasBusy;
        bool stopped = !busy && (_wasBusy || !animate);
        _wasBusy = busy;
        if (busy)
        {
            if (_progress is null)
            {
                _progress = new AiProgress(18, 13, "SubtleTextBrush") { Margin = new Thickness(0, 2, 0, 12) };
                _progress.SetState(host.State, confirmation is not null, animate: false);
                if (animate) UiMotion.Enter(_progress, offset: 3);
                Activity.Children.Insert(0, _progress);
            }
            else
            {
                _progress.SetState(host.State, confirmation is not null, animate);
                if (started) UiMotion.Fade(_progress, 1);
            }
        }
        else if (stopped && _progress is { } progress)
        {
            void Remove()
            {
                if (_wasBusy || _progress != progress) return;
                Activity.Children.Remove(progress);
                _progress = null;
            }
            if (animate) UiMotion.Fade(progress, 0, UiMotion.Quick, Remove);
            else Remove();
        }

        if (confirmation == _confirmation) return;
        _confirmation = confirmation;
        if (_consentCard is { } oldCard)
        {
            oldCard.IsEnabled = false;
            if (animate && confirmation is null) UiMotion.Fade(oldCard, 0, UiMotion.Quick, () => Activity.Children.Remove(oldCard));
            else Activity.Children.Remove(oldCard);
            _consentCard = null;
        }
        if (confirmation is null) return;
        _consentCard = ConsentCard(host, confirmation);
        if (animate) UiMotion.Enter(_consentCard);
        Activity.Children.Add(_consentCard);
        if (_consentNo is { } no) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => no.Focus()); // Enter = 취소, Tab → 진행
    }

    Button? _consentNo;

    FrameworkElement ConsentCard(AiHost host, string summary)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
        var stack = new StackPanel();
        var head = new TextBlock { Text = "Qutra가 이 작업을 하려고 합니다", FontSize = 12 };
        head.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
        var what = new TextBlock { Text = summary, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
        what.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new Button { Content = "취소", Style = (Style)FindResource("SiriFlatButton"), Margin = new Thickness(0, 0, 8, 0) };
        no.Click += (_, _) => host.Answer(false);
        _consentNo = no;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(card, System.Windows.Automation.AutomationLiveSetting.Assertive);
        System.Windows.Automation.AutomationProperties.SetName(card, "Qutra가 이 작업을 하려고 합니다: " + summary);
        var yes = new Button { Content = "진행", Style = (Style)FindResource("SiriFlatButton"), Foreground = Brushes.White };
        yes.SetResourceReference(BackgroundProperty, "AccentBrush");
        yes.Click += (_, _) => host.Answer(true);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        stack.Children.Add(head);
        stack.Children.Add(what);
        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }

    // ---------- input ----------

    void UpdateInput()
    {
        bool empty = Input.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        bool busy = _host?.IsBusy == true;
        // An empty box offers the microphone; once there is something to send, the arrow.
        bool send = !empty || _attachments.Count > 0;
        SendButton.Content = _voice.Listening ? "" : send ? "" : "";
        SendButton.ToolTip = _voice.Listening ? "듣기 중지" : send ? "보내기" : "말하기";
        SendButton.IsEnabled = !(busy && send);
        Chips.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Input_TextChanged(object sender, TextChangedEventArgs e) => UpdateInput();

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A key the IME consumed (confirming a Hangul syllable) arrives as ImeProcessed, never as Enter: no double send.
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Send();
        }
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        if (Input.Text.Trim().Length == 0 && _attachments.Count == 0) _ = ToggleListeningAsync();
        else Send();
    }

    void Send()
    {
        var text = Input.Text.Trim();
        if (_host is null || _host.IsBusy || (text.Length == 0 && _attachments.Count == 0)) return;
        var conv = _conv;
        if (conv is null)
        {
            conv = _host.Store.Create();
            _conv = conv;
            ConversationStarted?.Invoke(conv);
        }
        var files = _attachments.ToList();
        _attachments.Clear();
        Chips.Children.Clear();
        Input.Clear();
        _stick = true;
        _ = _host.SendAsync(conv, text.Length > 0 ? text : "첨부한 파일을 봐 주세요.", files);
    }

    void Retry()
    {
        if (_host is null || _conv is null || _host.IsBusy) return;
        var last = _host.Store.MessagesOf(_conv).LastOrDefault(m => m.Role == ChatRole.User);
        if (last is not null) _ = _host.SendAsync(_conv, last.Text, last.Attachments.ToList());
    }

    // ---------- attachments ----------

    void Add_Click(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = AddButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        AddMenu.IsOpen = true;
    }

    void AddPhoto_Click(object sender, RoutedEventArgs e) => Pick("이미지|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|모든 파일|*.*");
    void AddFile_Click(object sender, RoutedEventArgs e) => Pick("모든 파일|*.*");

    void Pick(string filter)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "추가" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddAttachments(dlg.FileNames);
    }

    static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (_attachments.Exists(a => a.Path == path)) continue;
            var a = new Attachment(ImageTypes.Contains(Path.GetExtension(path)) ? "image" : "file", path, Path.GetFileName(path));
            _attachments.Add(a);
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6), Tag = a };
            chip.SetResourceReference(Border.BackgroundProperty, "GlyphBackBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = a.Name, FontSize = 12, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var x = new Button { Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0), Style = (Style)FindResource("SiriCircleButton") };
            x.Click += (_, _) => { _attachments.Remove(a); Chips.Children.Remove(chip); UpdateInput(); };
            row.Children.Add(name);
            row.Children.Add(x);
            chip.Child = row;
            Chips.Children.Add(chip);
            if (IsVisible) UiMotion.Enter(chip, offset: 3, duration: UiMotion.Quick);
        }
        UpdateInput();
    }

    // ---------- microphone ----------

    async Task ToggleListeningAsync()
    {
        var text = await _voice.ListenAsync(Window.GetWindow(this));
        if (text is null) return;
        Input.Text += (Input.Text.Length > 0 ? " " : "") + text;
        Input.CaretIndex = Input.Text.Length;
    }

    void OnListeningChanged()
    {
        // Fading in and out shows that something is being heard; the level itself is not exposed.
        if (_voice.Listening && UiMotion.Enabled)
            SendButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.68, TimeSpan.FromMilliseconds(720))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        else UiMotion.Fade(SendButton, 1, UiMotion.Quick);
        UpdateInput();
    }
}
