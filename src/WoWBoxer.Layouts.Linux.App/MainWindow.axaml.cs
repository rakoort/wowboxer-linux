using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using WoWBoxer.Layouts.App.ViewModels;
using WoWBoxer.Layouts.Linux;
using PixelRect = WoWBoxer.Layouts.Core.PixelRect;

namespace WoWBoxer.Layouts.App;

public partial class MainWindow : Window, IUserDialogs
{
    /// <summary>The frame's outer band that resizes the window (upstream's WindowChrome ResizeBorderThickness).</summary>
    private const double ResizeBorder = 6;

    public MainViewModel ViewModel { get; }

    public MainWindow(AppSettings settings, bool paused = false)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(settings, this, paused);
        ViewModel.FirstRunRequested += () => FirstRunWindow.Show(this);
        ViewModel.ReportRequested += () => ReportWindow.Show(this, ViewModel.BuildReport(), ViewModel.SaveFullReport, Community.Links.DiscordHelp, url => ViewModel.OpenUrlCommand.Execute(url));
        DataContext = ViewModel;

        Opened += (_, _) =>
        {
            if (Screens is { } screens) screens.Changed += OnDisplaySettingsChanged;
        };
        Closed += (_, _) =>
        {
            if (Screens is { } screens) screens.Changed -= OnDisplaySettingsChanged;
        };

        // No system frame: the outer band of the window resizes it.
        AddHandler(PointerPressedEvent, OnFramePressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnFrameMoved, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Enter in a text box commits it (bindings update on lost focus).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is TextBox box)
                BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // Each page fades and slides in when its tab is chosen.
        foreach (var page in Pages.Children)
            page.PropertyChanged += (s, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true && ViewModel.Animations) AnimateIn((Control)s!);
            };

        ViewModel.Status.Info("Ready. This app only moves and resizes windows – it never sends keys or clicks to the game.");

        // The activity log shows its newest line. Scroll after the list has processed the change (re-entrant otherwise).
        ViewModel.Status.Lines.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (StatusList.ItemCount > 0) StatusList.ScrollIntoView(StatusList.ItemCount - 1);
            }, DispatcherPriority.Background);

        ActivityToggle.IsCheckedChanged += (_, _) => ActivityPopup.IsOpen = ActivityToggle.IsChecked == true;

        // While started: new windows go to their slot, rows, statuses, "on top" and Auto-fit follow what happens while
        // the app runs, also in the tray. Skipped while a squad row is dragged.
        var live = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        live.Tick += (_, _) =>
        {
            if (!Views.SetupView.Dragging) ViewModel.Tick();
        };
        live.Start();
        Closed += (_, _) => live.Stop();
    }

    private static readonly Animation PageIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(260),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.None,
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 14d) } },
            new KeyFrame { Cue = new Cue(0.85), Setters = { new Setter(OpacityProperty, 1d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
        },
    };

    private static void AnimateIn(Control page)
    {
        page.RenderTransform = new TranslateTransform();
        _ = PageIn.RunAsync(page);
    }

    // ---------- custom chrome: drag by the title bar, resize by the frame's edge ----------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Handled) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximised();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;
        var (w, h) = (Bounds.Width, Bounds.Height);
        bool left = p.X < ResizeBorder, right = p.X > w - ResizeBorder, top = p.Y < ResizeBorder, bottom = p.Y > h - ResizeBorder;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }

    private void OnFramePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || EdgeAt(e.GetPosition(this)) is not { } edge) return;
        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnFrameMoved(object? sender, PointerEventArgs e)
    {
        Frame.Cursor = EdgeAt(e.GetPosition(this)) switch
        {
            WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => null,
        };
    }

    private void ToggleMaximised() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximised();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnActivityClosed(object? sender, EventArgs e) => ActivityToggle.IsChecked = false;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ViewModel.RefreshMonitors);

    // ---------- IUserDialogs ----------

    public string? Prompt(string title, string message, string initial) => PromptWindow.Show(this, title, message, initial);

    public bool Confirm(string title, string message) => PromptWindow.Confirm(this, title, message);

    public int Choose(string title, string message, IReadOnlyList<string> buttons) => PromptWindow.Choose(this, title, message, buttons);

    public string? AskShareCode(Func<string, (bool Ok, string Message)> check) => PromptWindow.AskShareCode(this, check);

    public string? OpenFile(string filter)
    {
        var files = Wait(StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = FileTypes(filter), AllowMultiple = false }));
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public string? SaveFile(string filter, string fileName)
    {
        var file = Wait(StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { FileTypeChoices = FileTypes(filter), SuggestedFileName = fileName }));
        return file?.TryGetLocalPath();
    }

    /// <summary>A WPF file dialog filter ("Layout files (*.json)|*.json|…") as Avalonia file types.</summary>
    private static List<FilePickerFileType> FileTypes(string filter)
    {
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        return types;
    }

    /// <summary>Waits for a picker in a nested dispatcher loop (WPF's ShowDialog), so the view model's call stays simple.</summary>
    private static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    public void CopyToClipboard(string text) => _ = Clipboard?.SetTextAsync(text);

    /// <summary>Big numbers on top of each WoW window for a few seconds. They never take focus.</summary>
    public void ShowIdentify(IReadOnlyList<(PixelRect Rect, string Number, string Caption)> labels)
    {
        var overlays = labels.Select(l => IdentifyOverlay.Show(l.Rect, l.Number, l.Caption)).ToList();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var o in overlays) o.Close();
        };
        timer.Start();
    }
}

/// <summary>
/// A transparent label over a WoW window (Identify). Never activated, sends nothing to the game. WoWBoxer's KWin script
/// puts it over the window and keeps it above, frameless and off the taskbar (the app's own "helper window").
/// </summary>
internal static class IdentifyOverlay
{
    private static int _count;

    public static Window Show(PixelRect rect, string number, string caption)
    {
        var gold = Dialogs.Resource<IBrush>("GoldHiBrush");
        var title = $"WoWBoxer Identify {Interlocked.Increment(ref _count)}";
        var window = new Window
        {
            Title = title,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            CanResize = false,
            Focusable = false,
            IsHitTestVisible = false,
            Width = rect.Width,
            Height = rect.Height,
            Content = new Border
            {
                BorderBrush = gold,
                BorderThickness = new Thickness(3),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x0B, 0x0E, 0x14)),
                    BorderBrush = Dialogs.Resource<IBrush>("GoldBrush"),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(40, 10, 40, 18),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = number, FontSize = 110, Foreground = gold, HorizontalAlignment = HorizontalAlignment.Center,
                                FontFamily = Dialogs.Resource<FontFamily>("DisplayFont") ?? FontFamily.Default },
                            new TextBlock { Text = caption, FontSize = 22, Foreground = Dialogs.Resource<IBrush>("ParchmentBrush"), HorizontalAlignment = HorizontalAlignment.Center,
                                FontFamily = Dialogs.Resource<FontFamily>("DisplaySemiFont") ?? FontFamily.Default },
                        },
                    },
                },
            },
        };
        window.Closed += (_, _) => WindowPlacer.RemoveOverlay(title);
        window.Show();
        WindowPlacer.ShowAsOverlay(title, rect);   // KWin logical pixels, above, frameless, off the taskbar, never activated
        return window;
    }
}
