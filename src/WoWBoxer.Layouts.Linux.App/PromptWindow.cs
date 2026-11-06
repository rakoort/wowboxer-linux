using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace WoWBoxer.Layouts.App;

/// <summary>Small dialogs in the War Table style: a one-line prompt and a yes/no question.</summary>
internal static class PromptWindow
{
    public static string? Show(Window owner, string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 420, Margin = new Thickness(0, 10, 0, 18) };
        AutomationProperties.SetAutomationId(box, "PromptText");
        var result = Dialog(owner, title, message, box, "OK", "Cancel");
        return result ? box.Text ?? "" : null;
    }

    public static bool Confirm(Window owner, string title, string message) => Dialog(owner, title, message, null, "Yes", "No");

    /// <summary>One question, several answers (the first is the default). Returns the button's index, or -1 for Esc / closed.</summary>
    public static int Choose(Window owner, string title, string message, IReadOnlyList<string> buttons)
    {
        var result = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Window? window = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], IsDefault = i == 0, MinWidth = 110, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(b, $"Choice{i}");
            b.Click += (_, _) =>
            {
                result = index;
                window!.Close();
            };
            row.Children.Add(b);
        }
        var body = new StackPanel
        {
            Width = 460,
            Children =
            {
                Dialogs.Text(title, "H2", new Thickness(0, 0, 0, 10)),
                Dialogs.Text(message, "Body", new Thickness(0, 0, 0, 20)),
                row,
            },
        };
        window = Dialogs.Frame(owner, title, body);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        window.Opened += (_, _) => ((Button)row.Children[0]).Focus();
        Dialogs.ShowModal(window, owner);
        return result;
    }

    /// <summary>
    /// "Paste a layout code": fixed width, the code wraps inside a box that scrolls instead of growing, OK and Cancel always
    /// in view, a Paste button, a live check under the box, Enter = OK when the code is valid.
    /// </summary>
    public static string? AskShareCode(Window owner, Func<string, (bool Ok, string Message)> check)
    {
        var box = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            Height = 120,
            FontFamily = Dialogs.Resource<FontFamily>("MonoFont") ?? FontFamily.Default,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(box, "ShareCodeText");
        AutomationProperties.SetName(box, "Layout code");
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 16) };
        AutomationProperties.SetAutomationId(status, "ShareCodeCheck");
        var ok = new Button { Content = "OK", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 100 };
        var paste = new Button { Content = "Paste", MinWidth = 90 };
        ToolTip.SetTip(paste, "Paste the code from the clipboard");
        AutomationProperties.SetAutomationId(ok, "ShareCodeOk");
        AutomationProperties.SetAutomationId(paste, "ShareCodePaste");
        var valid = false;
        void Check()
        {
            var text = box.Text ?? "";
            var (good, message) = check(text);
            var empty = text.Trim().Length == 0;
            valid = good;
            ok.IsEnabled = good;
            status.Text = empty ? "Paste a code that starts with WBX1:" : message;
            status.Foreground = Dialogs.Resource<IBrush>(empty ? "MutedBrush" : good ? "OkBrush" : "NoBrush");
        }
        box.TextChanged += (_, _) => Check();
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(paste, Dock.Left);
        buttons.Children.Add(paste);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { ok, cancel } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        var body = new StackPanel
        {
            Width = 520,
            Children =
            {
                Dialogs.Text("Paste a layout code", "H2", new Thickness(0, 0, 0, 10)),
                Dialogs.Text("Paste a WBX1: code from a friend or the website:", "Body", default),
                box,
                status,
                buttons,
            },
        };
        var window = Dialogs.Frame(owner, "Paste a layout code", body);
        AutomationProperties.SetAutomationId(window, "ShareCodeDialog");
        string? result = null;
        void Accept()
        {
            if (!valid) return;
            result = box.Text;
            window.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => window.Close();
        paste.Click += async (_, _) =>
        {
            if (window.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) box.Text = text.Trim();
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };
        // Enter = OK when the code is valid (never a new line inside the code)
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Accept();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Opened += (_, _) =>
        {
            Check();
            box.Focus();
        };
        Dialogs.ShowModal(window, owner);
        return result;
    }

    public static bool Confirm(Window owner, string title, string message, string yes, string no) => Dialog(owner, title, message, null, yes, no);

    private static bool Dialog(Window owner, string title, string message, Control? input, string yes, string no)
    {
        var ok = new Button { Content = yes, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = no, IsCancel = true, MinWidth = 100 };
        var body = new StackPanel
        {
            Children =
            {
                Dialogs.Text(title, "H2", new Thickness(0, 0, 0, 10)),
                Dialogs.Text(message, "Body", input is null ? new Thickness(0, 0, 0, 20) : default),
            },
        };
        if (input is not null) body.Children.Add(input);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });

        var window = Dialogs.Frame(owner, title, body);
        var result = false;
        ok.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            if (input is TextBox t)
            {
                t.Focus();
                t.SelectAll();
            }
            else ok.Focus();
        };
        Dialogs.ShowModal(window, owner);
        return result;
    }
}

/// <summary>The War Table dialog frame and helpers shared by the app's code-built windows.</summary>
internal static class Dialogs
{
    /// <summary>A theme resource (brush, font) from the app's War Table styles; null outside the app (tests).</summary>
    public static T? Resource<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as T : null;

    /// <summary>The app's icon (Assets/app.png; the teal one in a dev build), for windows and the tray.</summary>
    public static WindowIcon AppIcon => _icon ??= new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://wowboxer/Assets/app.png")));
    private static WindowIcon? _icon;

    /// <summary>A text link (the theme's "Hyperlink" button) that opens <paramref name="url"/> in the browser.</summary>
    public static Button Link(string text, string url)
    {
        var link = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Center };
        if (Resource<ControlTheme>("Hyperlink") is { } theme) link.Theme = theme;
        link.Click += (_, _) => Infrastructure.Shell.OpenUrl(url);
        return link;
    }

    /// <summary>The ornate panel (the theme's "OrnatePanel" control theme) around <paramref name="content"/>.</summary>
    public static ContentControl Ornate(object content, Thickness padding)
    {
        var panel = new ContentControl { Padding = padding, Content = content };
        if (Resource<ControlTheme>("OrnatePanel") is { } theme) panel.Theme = theme;
        return panel;
    }

    /// <summary>A text in one of the theme's text styles (control themes "H2", "Body" …).</summary>
    public static TextBlock Text(string text, string style, Thickness margin)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        if (Resource<ControlTheme>(style) is { } theme) block.Theme = theme;
        return block;
    }

    /// <summary>The War Table dialog frame around <paramref name="body"/>: no system frame, dragged by its top 40 px.</summary>
    public static Window Frame(Window? owner, string title, Control body) =>
        Chrome(owner, title, Ornate(body, new Thickness(26, 22, 26, 22)), "NightBrush", 12, 10, 40);

    /// <summary>
    /// A frameless, transparent window with a rounded <paramref name="background"/> card around <paramref name="child"/>,
    /// moved by dragging its top <paramref name="caption"/> pixels (WPF's WindowChrome caption).
    /// </summary>
    public static Window Chrome(Window? owner, string title, Control child, string background, double margin, double radius, double caption)
    {
        var border = new Border
        {
            Background = Resource<IBrush>(background),
            CornerRadius = new CornerRadius(radius),
            Margin = new Thickness(margin),
            Child = child,
        };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Foreground = Resource<IBrush>("TextBrush"),
            FontFamily = Resource<FontFamily>("BodyFont") ?? FontFamily.Default,
            Icon = AppIcon,
            Content = border,
        };
        border.PointerPressed += (_, e) =>
        {
            if (e.GetPosition(border).Y < caption && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };
        return window;
    }

    /// <summary>
    /// Shows <paramref name="window"/> modal and waits for it to close (a nested dispatcher loop, like WPF's ShowDialog),
    /// so the view models' questions stay simple calls. Without a visible owner it opens as a normal window.
    /// </summary>
    public static void ShowModal(Window window, Window? owner)
    {
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        if (owner is { IsVisible: true }) _ = window.ShowDialog(owner);
        else window.Show();
        Dispatcher.UIThread.PushFrame(frame);
    }
}
