using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace WoWBoxer.Layouts.App;

/// <summary>"What's new": shown once on the first start after an update, so the owner can see what changed.</summary>
internal static class WhatsNewWindow
{
    /// <summary>
    /// The newest changes first. Keep it to what a player notices; only the first <see cref="Shown"/> are listed. Upstream's
    /// entries about its update channels and update prompt are left out: on Linux, updates come with the system.
    /// </summary>
    public static readonly (string Title, string Text)[] Changes =
    [
        ("Window swap keeps its main slot",
            "Dragging a window of Main + side no longer loses the main slot, and Custom layouts use their clearly largest window. Without a main slot, a swap hotkey shows a note in the status bar and the crowns on the map pulse. Same size in Advanced editing is now Ctrl+Shift+M (the NVIDIA overlay takes Alt+Z)."),
        ("Fill a custom area",
            "Advanced editing can now fill the empty space next to your other windows, the span of the selection, or a rectangle you draw – as a row, column or grid, with a live preview and each window's action-bar verdict before you apply."),
        ("Custom layouts and Advanced editing",
            "Free for all is now Custom: drag any window of a preset and it becomes your own layout, kept as a draft on every change. ADVANCED EDITING on the map selects several windows and lines them up – same size, align, distribute, fill a row – with undo and redo."),
        ("Paste a layout code, fixed",
            "Long codes stay inside the dialog now, with a Paste button and a live check (\"Valid WBX1 code · 3 clients · Main + side\")."),
        ("Remove title bars, fixed",
            "Thanks to Limerobin: borderless windows are now exactly the game area, and Main + side fills its column without gaps. New switch on Setup (Your squad); still off by default."),
        ("Window swap hotkeys (beta, advanced)",
            "Settings › Advanced, off by default: hotkeys move a client's window into the main slot (Ctrl+Alt+Shift+Right/Left, +1…0, +Backspace for home). Only windows move – nothing is sent to the game. Blizzard has not ruled on window-swap tools; use at your own judgement."),
        ("Experimental switches",
            "New features that could still misbehave will arrive in Settings › Experimental, switched off – turn one on to try it, off again if it causes trouble."),
        ("Now simply called WoWBoxer",
            "The app's name is now just WoWBoxer – in the title bar, the tray, the Start menu, your shortcuts and Windows' Apps list. Your layouts and settings stay as they are. The in-game addon is the WoWBoxer AddOn."),
        ("New Help tab",
            "Everything for when something goes wrong, in one place: common problems with their fixes, Report a problem (a short report you see first, pasted by you in #setup-help), Diagnose, the logs and the Discord, YouTube and website links. F1 opens it."),
        ("Up to 10 clients – two parties",
            "Pick 1–10 clients. Clients 1–5 are party 1, 6–10 party 2 (client 6 is its main); Your squad groups the rows by party."),
        ("Layout and monitors: two clear choices",
            "Under the layout cards, Monitors picks 1, 2 or 3 monitors (and which ones). Every layout works across them: Main + side puts the main on monitor 1 and the followers on the others; Custom lets you drag windows across monitors."),
        ("Hover to activate starts at 30 ms",
            "Turning hover to activate on now sets the delay to 30 ms, the default. Move the slider for faster or slower; Reset to default next to it puts 30 ms back. Closing WoWBoxer turns it off again (Settings can keep it on)."),
        ("Hover to activate: the WoWBoxer way to switch",
            "Point at a window and it becomes active – Windows' own setting, in Your squad, Settings or the tray. It sends nothing to the game; WoWBoxer never sends keys or clicks. Off until you turn it on, and your own Windows values come back with one click, or when you uninstall."),
        ("New website address",
            "The Guide and rules links now open wowboxer.com."),
        ("Start and Stop",
            "The app now opens stopped and leaves every window alone until you press Start placing windows. Stop (next to the main button, or the tray) leaves them alone again. A desktop layout shortcut starts at once; Settings can start it whenever the app opens."),
        ("Plug and play",
            "The app always watches: every WoW window that opens goes straight into the next free slot. Each slot shows Awaiting… until its window is there, then Connected. Nothing to start, no live switch – Pause is in the tray menu."),
        ("One button",
            "\"Open N WoW clients\" opens a login screen for each empty slot (you log in yourself). When all are connected it becomes Re-arrange. \"Open Battle.net instead\" sits next to it."),
        ("Any WoW install",
            "No game picker: Classic Era, Forever, Retail … are found by their install folder. If two games are open, the squad follows the one with the most windows; the other's windows are left alone (Include adds them)."),
        ("Less on screen",
            "Squad rows are one line – ▾ opens class, account, always on top and Identify. Window rules, UI Scale and tools moved to Settings; the activity log opens from the status bar."),
    ];

    /// <summary>How many changes the window lists (the rest is in the CHANGELOG).</summary>
    private const int Shown = 6;

    public static bool ShouldShow(AppSettings settings, string version) =>
        settings.FirstRunDone && !string.Equals(settings.LastSeenVersion, version, StringComparison.Ordinal);

    public static void Show(Window? owner, string version)
    {
        IBrush? R(string key) => Dialogs.Resource<IBrush>(key);

        var list = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        foreach (var (title, text) in Changes.Take(Shown))
        {
            var head = Dialogs.Text(title, "H2", new Thickness(0, 8, 0, 2));
            head.FontSize = 14;
            list.Children.Add(head);
            list.Children.Add(new TextBlock { Text = text, Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
        }
        // the list scrolls; the header and Continue always stay in view
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0), Focusable = false };

        var ok = new Button
        {
            Content = new TextBlock { Text = "CONTINUE" },
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        if (Dialogs.Resource<ControlTheme>("EnterWorldButton") is { } enter) ok.Theme = enter;
        var heading = Dialogs.Text("What's new", "H1", new Thickness(0, 6, 0, 0));
        heading.FontSize = 24;
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        var divider = new ContentControl { Margin = new Thickness(100, 12, 100, 0) };
        if (Dialogs.Resource<ControlTheme>("Divider") is { } line) divider.Theme = line;
        var header = new StackPanel
        {
            Children =
            {
                new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://wowboxer/Assets/app.png"))), Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center },
                heading,
                new TextBlock { Text = $"WoWBoxer {version}", Foreground = R("GoldBrush"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) },
                divider,
            },
        };
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        // one quiet line at the bottom: where to talk about it
        var discord = FirstRunWindow.DiscordLink();
        discord.FontSize = 13;
        discord.Margin = new Thickness(0, 12, 0, 0);
        var bottom = new StackPanel { Children = { discord, ok } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(header);
        content.Children.Add(bottom);
        content.Children.Add(scroll);
        var window = Dialogs.Chrome(owner, "What's new in WoWBoxer", Dialogs.Ornate(content, new Thickness(32, 22, 32, 22)), "WarTableBrush", 14, 12, 60);
        window.Width = 620;
        window.SizeToContent = SizeToContent.Height;
        // never taller than the screen (a long list used to push Continue off it)
        var workArea = (owner?.Screens.ScreenFromWindow(owner) ?? owner?.Screens.Primary)?.WorkingArea.Height / (owner?.DesktopScaling ?? 1);
        window.MaxHeight = Math.Min(640, (workArea ?? 900) - 40);
        AutomationProperties.SetAutomationId(window, "WhatsNew");
        ok.Click += (_, _) => window.Close();
        Dialogs.ShowModal(window, owner);
    }
}
