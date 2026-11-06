using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using WoWBoxer.Layouts.App.ViewModels;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;
using Path = System.IO.Path;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// "--screenshots FOLDER" (always in demo mode): renders each screen of the app at 1440×900 as PNG, plus
/// <c>shots.json</c> (file, alt text, caption, app version). tools/make-screenshots.ps1 turns them into WebP and writes
/// the website's manifest.json. The window stays off-screen and never takes focus; the pictures come from Avalonia's
/// own renderer, so they do not depend on the screen, its scale or what is on top of it.
/// </summary>
internal static class Screenshots
{
    public const int Width = 1440, Height = 900;

    public sealed record Shot(string File, string Title, string Alt, string Caption);

    public static async Task RunAsync(MainWindow main, string folder, Func<NativeMenu> trayMenu)
    {
        Directory.CreateDirectory(folder);
        var vm = main.ViewModel;
        // No frame: the desktop then never limits the window to the (possibly small) screen.
        main.WindowDecorations = WindowDecorations.None;
        main.CanResize = false;
        main.ShowActivated = false;
        main.ShowInTaskbar = false;
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Position = new PixelPoint(-20000, -20000);
        main.Width = Width;
        main.Height = Height;
        // The app's content is laid out at exactly 1440×900 on a canvas, whatever size the window itself gets (a desktop
        // may still keep a window within its screen). Only the content is rendered.
        var content = (Control)main.Content!;
        main.Content = null;
        content.Width = Width;
        content.Height = Height;
        main.Content = new Canvas { Children = { content } };
        main.Show();

        var shots = new List<Shot>();
        async Task Snap(string name, string title, string alt, string caption, Func<Bitmap>? render = null)
        {
            if (vm.SelectedTab == AppTab.Setup) await InPlace(vm);
            await Settle();
            using var bitmap = render?.Invoke() ?? Render(content);
            Save(bitmap, Path.Combine(folder, name + ".png"));
            shots.Add(new Shot(name, title, alt, caption));
        }

        // 1) Setup: 3 clients, Main + side, 1 monitor
        Squad(vm, 3);
        Use(vm, PresetKind.Side, monitors: 1);
        vm.SelectedTab = AppTab.Setup;
        vm.SelectedSlotIndex = -1;
        await Snap("setup-main-side", "Setup: clients, layout and monitors", "WoWBoxer, Setup: three WoW windows in the Main + side layout on one monitor – the main window big on the left, two followers stacked on the right – and the squad list with every window connected.",
            "Pick how many clients and a layout – every window goes to its place as it opens.");

        // 2) Setup: 2 monitors, Custom
        Squad(vm, 4);
        Use(vm, PresetKind.Custom, monitors: 2);
        // a hand-made arrangement; window 2 straddles the monitor border (Custom allows it)
        Place(vm, 0, 0, 0, 1840, 1150);
        Place(vm, 1, 1960, 560, 1100, 740);
        Place(vm, 2, 3160, 40, 1260, 440);
        Place(vm, 3, 3160, 520, 1260, 440);
        vm.SelectedSlotIndex = -1;
        await Snap("setup-two-monitors-free", "Two monitors, Custom", "WoWBoxer Setup with two monitors (2560×1440 and 1920×1080) and the Custom layout: four WoW windows arranged by hand over both monitors on the map – window 2 straddles the border between them.",
            "Two monitors, Custom: drag any window anywhere – across monitors too.");

        // 3) Setup: 10 clients, two parties
        Squad(vm, 10);
        Use(vm, PresetKind.Side, monitors: 2);
        vm.SelectedSlotIndex = -1;
        await Snap("setup-ten-clients", "Ten clients in two parties", "WoWBoxer Setup with ten WoW clients in two parties of five over two monitors; the squad list is grouped into Party 1 and Party 2.",
            "Up to 10 clients: two parties of five, each with its own main.");

        // 4) Window details (the inspector of one squad row)
        Squad(vm, 3);
        Use(vm, PresetKind.Side, monitors: 1);
        vm.SelectedSlotIndex = 0;
        vm.ExpandedIndex = 0;
        await Snap("window-details", "Window details", "WoWBoxer Setup with the details of window 1 open: name, class, position and size in pixels, always on top, and whether the whole action bar fits.",
            "Fine-tune any window: exact position and size, on top, and an action-bar check.");
        vm.ExpandedIndex = -1;

        // 4b) Advanced editing: the ribbon, three windows selected and filled into one row (that makes it Custom)
        vm.SelectedSlotIndex = -1;
        vm.AdvancedEditing = true;
        vm.SetSelection([1, 0, 2]);
        vm.FillRowCommand.Execute(null);
        await Snap("advanced-editing", "Advanced editing", "WoWBoxer Setup with Advanced editing on: a toolbar above the map to match sizes, align, distribute and fill a row or column; three WoW windows are selected (window 2 with the gold reference outline) and fill the monitor's width side by side.",
            "Advanced editing: select several windows and line them up – same size, align, distribute, fill a row – with undo and redo.");
        vm.UndoCommand.Execute(null);   // back to Main + side (no question: nothing unsaved to leave)
        vm.SetSelection([]);
        vm.AdvancedEditing = false;

        // 5) Settings: hover to activate and the action-bar fit
        vm.SelectedTab = AppTab.Settings;
        await Snap("settings", "Settings: hover to activate and action bar", "WoWBoxer Settings: hover to activate windows (done by WoWBoxer's KWin script) with its delay, the action-bar fit (UI Scale) and the window rules.",
            "Hover to activate is done by WoWBoxer's KWin script – it sends nothing to the game. Auto-fit keeps the whole action bar in view.");

        // 6) Help: Report a problem
        vm.SelectedTab = AppTab.Help;
        await Snap("help", "Help and Report a problem", "WoWBoxer Help tab: the Report a problem button, links to the WoWBoxer Discord, YouTube and website, tools to see what the app sees, and common problems with their fixes.",
            "Stuck? Common problems with their fixes, and Report a problem for #setup-help on Discord.");

        // 7) About and the community links
        vm.SelectedTab = AppTab.About;
        await Snap("about", "About and community links", "WoWBoxer About tab: version, MIT licence, privacy (no telemetry) and the Discord, YouTube and website links.",
            "Free and open source (MIT). No telemetry – your layouts and logs stay on your PC.");

        // 8) The tray menu, over the app's corner as it pops up from the panel
        vm.SelectedTab = AppTab.Setup;
        vm.SelectedSlotIndex = -1;
        await Snap("tray-menu", "Tray menu", "The WoWBoxer tray menu: Open WoWBoxer, Open missing WoW clients, Re-arrange, Identify windows, Stop placing windows, Hover to activate windows, Report a problem and Quit.",
            "Everything you need mid-raid is one right-click away in the tray.", () =>
            {
                using var app = Render(content);
                return WithTrayMenu(app, trayMenu());
            });

        // 9) First start: the welcome screen over the app
        vm.SelectedTab = AppTab.Setup;
        await Snap("first-run", "Welcome screen", "WoWBoxer welcome screen on first start: what the app does (places WoW windows) and what it never does (no keys or clicks sent to the game, no memory reading, no injection).",
            "What it does – and what it never does.", () =>
            {
                using var app = Render(content);
                var welcome = FirstRunWindow.Create(null);
                welcome.ShowActivated = false;
                welcome.ShowInTaskbar = false;
                welcome.WindowStartupLocation = WindowStartupLocation.Manual;
                welcome.Position = new PixelPoint(-20000, -20000);
                welcome.Show();
                welcome.UpdateLayout();
                using var dialog = Render(welcome);
                welcome.Close();
                return Compose(app, dialog, Color.FromArgb(0xB0, 0x05, 0x07, 0x0B), null);
            });

        var json = JsonSerializer.Serialize(new { version = MainViewModel.AppVersion, shots },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(folder, "shots.json"), json);
    }

    /// <summary><paramref name="count"/> open demo windows and the made-up names and classes on every slot.</summary>
    private static void Squad(MainViewModel vm, int count)
    {
        Sandbox.SetWindowCount(count);
        vm.ClientCount = count;
    }

    private static void Use(MainViewModel vm, PresetKind kind, int monitors)
    {
        vm.SelectedLayout = vm.Layouts.First(c => c.Kind == kind);
        vm.SelectedMonitorCount = vm.MonitorCounts[monitors - 1];
        for (var i = 0; i < vm.ClientCount; i++)
        {
            vm.SelectedSlotIndex = i;
            if (vm.SlotName != Demo.Squad[i].Name) vm.SlotName = Demo.Squad[i].Name;
            if (vm.SlotClass.Id != Demo.Squad[i].Class) vm.SlotClass = WowClasses.Find(Demo.Squad[i].Class)!;
        }
    }

    /// <summary>
    /// Re-arranges, then waits until every demo window is in its slot ("Connected"), so the squad shows no
    /// "Placing…" and the activity line is the same on every run.
    /// </summary>
    private static async Task InPlace(MainViewModel vm)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (vm.ArrangeCommand.CanExecute(null)) vm.ArrangeCommand.Execute(null);
            for (var i = 0; i < 40 && (vm.Busy || vm.ConnectedCount < vm.ClientCount); i++) await Task.Delay(250);
            if (!vm.Busy && vm.ConnectedCount == vm.ClientCount) return;
        }
        throw new InvalidOperationException($"Only {vm.ConnectedCount} of {vm.ClientCount} demo windows are in place.");
    }

    private static void Place(MainViewModel vm, int slot, int x, int y, int width, int height)
    {
        vm.SelectedSlotIndex = slot;
        vm.SlotX = x;
        vm.SlotY = y;
        vm.SlotW = width;
        vm.SlotH = height;
    }

    /// <summary>Lets the app place the demo windows (its 1 s tick) and finish layout and data binding.</summary>
    private static async Task Settle()
    {
        await Task.Delay(1600);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static RenderTargetBitmap Render(Control element)
    {
        element.UpdateLayout();
        var size = element is TopLevel top ? top.ClientSize : element.Bounds.Size;
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(size.Width), (int)Math.Round(size.Height)), new Vector(96, 96));
        bitmap.Render(element);
        return bitmap;
    }

    /// <summary>
    /// <paramref name="overlay"/> drawn over the dimmed app: centred (the welcome screen) or, with <paramref name="at"/>,
    /// at that place with a soft drop shadow (the tray menu).
    /// </summary>
    private static RenderTargetBitmap Compose(Bitmap app, Bitmap overlay, Color dim, Point? at)
    {
        var size = overlay.PixelSize;
        var place = at ?? new Point(Math.Round((Width - size.Width) / 2.0), Math.Round((Height - size.Height) / 2.0));
        var canvas = new Canvas { Width = Width, Height = Height };
        canvas.Children.Add(new Image { Source = app, Width = Width, Height = Height });
        canvas.Children.Add(new Rectangle { Width = Width, Height = Height, Fill = new ImmutableSolidColorBrush(dim) });
        if (at is not null)
        {
            var shadow = new Rectangle { Width = size.Width, Height = size.Height, Fill = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)) };
            Canvas.SetLeft(shadow, place.X + 4);
            Canvas.SetTop(shadow, place.Y + 6);
            canvas.Children.Add(shadow);
        }
        var picture = new Image { Source = overlay, Width = size.Width, Height = size.Height };
        Canvas.SetLeft(picture, place.X);
        Canvas.SetTop(picture, place.Y);
        canvas.Children.Add(picture);
        canvas.Measure(new Size(Width, Height));
        canvas.Arrange(new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        bitmap.Render(canvas);
        return bitmap;
    }

    /// <summary>
    /// The tray menu at the bottom right of the app, where a bottom panel opens it. The desktop draws the real menu
    /// (D-Bus menu), so it is drawn here from the same items in upstream's War Table tray colours.
    /// </summary>
    private static RenderTargetBitmap WithTrayMenu(Bitmap app, NativeMenu menu)
    {
        IBrush Brush(byte a, byte r, byte g, byte b) => new ImmutableSolidColorBrush(Color.FromArgb(a, r, g, b));
        var text = Brush(0xFF, 0xD6, 0xCD, 0xB6);
        var list = new StackPanel { Margin = new Thickness(2) };
        foreach (var entry in menu.Items)
        {
            if (entry is NativeMenuItemSeparator)
            {
                list.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 3, 4, 3), Background = Brush(0x52, 0xC9, 0xA2, 0x4A) });
                continue;
            }
            if (entry is not NativeMenuItem { IsVisible: true } item) continue;
            var header = item.Header == "Start placing windows" ? "Stop placing windows" : item.Header;   // as it reads while started
            var check = item.Header == "Hover to activate windows" || item.IsChecked;
            list.Children.Add(new TextBlock
            {
                Text = (check ? "✓  " : "    ") + header,
                Foreground = text,
                FontSize = 13.3,
                Padding = new Thickness(10, 5, 24, 5),
            });
        }
        var panel = new Border
        {
            Background = Brush(0xFF, 0x15, 0x1A, 0x24),
            BorderBrush = Brush(0xFF, 0xC9, 0xA2, 0x4A),
            BorderThickness = new Thickness(1),
            Child = list,
        };
        Avalonia.Controls.Documents.TextElement.SetFontFamily(panel, Dialogs.Resource<FontFamily>("BodyFont") ?? FontFamily.Default);
        panel.Measure(Size.Infinity);
        var size = panel.DesiredSize;
        panel.Arrange(new Rect(size));
        using var picture = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        picture.Render(panel);
        var at = new Point(Width - picture.PixelSize.Width - 24, Height - picture.PixelSize.Height - 40);
        return Compose(app, picture, Color.FromArgb(0x70, 0x05, 0x07, 0x0B), at);
    }

    private static void Save(Bitmap bitmap, string path)
    {
        if (bitmap.PixelSize.Width != Width || bitmap.PixelSize.Height != Height)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}, not {Width}×{Height}.");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
