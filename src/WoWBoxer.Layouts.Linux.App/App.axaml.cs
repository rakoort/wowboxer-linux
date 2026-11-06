using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.App.ViewModels;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App;

/// <summary>
/// Start-up: one instance only (<see cref="Program"/>), optional "--tray" (start hidden in the tray), "--start NAME" (that
/// layout, and open the missing clients – used by desktop shortcuts), "--pause" / "--resume" (Stop / Start placing
/// windows; also sent to a running copy, e.g. from a script), first-run screen, tray icon.
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private PosixSignalRegistration? _sigterm, _sigint;

    /// <summary>How the D-Bus link to the agent started (set by <see cref="Program"/>; null in demo mode).</summary>
    public static AgentLinkStart? Link { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        // English UI with the same number format on every PC ("1.25", not "1,25").
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CommandManager.RequeryOnInput();
        desktop.Exit += (_, _) => OnExit();
        // A logout or `kill` quits like the tray's Quit, so the agent is unloaded and the swap keys are cleared.
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, QuitOnSignal);
        _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, QuitOnSignal);
        var args = desktop.Args ?? [];
        // inside the main loop, so the first-run and What's new dialogs can wait for their answer
        Dispatcher.UIThread.Post(() =>
        {
            if (DemoArgs(args) is { } demo) StartDemo(demo.Shots);
            else Start(args);
        });
    }

    private void Start(string[] args)
    {
        var settings = AppSettings.Load();
        var (startInTray, startProfile) = ParseArgs(args);
        // Opens stopped: nothing is moved until Start (another game may be open). A layout shortcut, "--resume" or the
        // Settings switch start it right away.
        var paused = PauseArg(args) ?? !(settings.StartPlacingOnOpen || startProfile is not null);

        if (!settings.FirstRunDone)
        {
            FirstRunWindow.Show(null);
            settings.FirstRunDone = true;
            settings.LastSeenVersion = MainViewModel.AppVersion;   // a fresh install has nothing "new"
            settings.Save();
            startInTray = false;
        }

        _main = new MainWindow(settings, paused);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // Log and keep running: a placement tool must not vanish in the middle of a squad start.
            _main.ViewModel.Status.Report(StatusLevel.Error, "Unexpected error: " + e.Exception.Message);
            LogCrash(e.Exception);
            e.Handled = true;
        };
        _main.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && _main.WindowState == WindowState.Minimized && _tray is not null) _main.Hide();   // minimise = to the tray
        };
        _main.Closed += (_, _) => Shutdown();
        AgentLink.ArgsReceived += a => Dispatcher.UIThread.Post(() => OnSecondInstance([.. a]));
        AgentLink.ShowRequested += () => Dispatcher.UIThread.Post(ShowMain);
        CreateTray();
        if (!startInTray) _main.Show();
        var version = MainViewModel.AppVersion;
        if (!startInTray && WhatsNewWindow.ShouldShow(settings, version))   // a tray start shows it next time
        {
            WhatsNewWindow.Show(_main, version);
            settings.LastSeenVersion = version;
            settings.Save();
        }
        if (Link == AgentLinkStart.Unavailable || AgentLink.Problem is not null)
            _main.ViewModel.Status.Warn(AgentLink.Problem ?? "WoWBoxer could not reach KWin: it needs a KDE Plasma 6 session.");
        if (AgentLink.OldScriptLoaded)
            _main.ViewModel.Status.Warn($"The KWin script \"{AgentLink.OldScriptName}\" is loaded too and also moves WoW windows. Turn it off in System Settings › Window Management › KWin Scripts before you press Start.");

        if (startProfile is not null) StartProfile(startProfile);
    }

    /// <summary>Unhandled errors go to the log file in full (the activity line shows only the message).</summary>
    private static void LogCrash(Exception error)
    {
        try
        {
            Directory.CreateDirectory(StatusLog.LogFolder);
            File.AppendAllText(StatusLog.LogFile, error + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is best effort
        }
    }

    /// <summary>True for "--demo" and "--screenshots": no D-Bus, no agent, no single-instance check.</summary>
    public static bool IsDemo(string[] args) => DemoArgs(args) is not null;

    /// <summary>"--demo" (made-up monitors and squad, nothing real touched) and "--screenshots FOLDER" (demo + render every screen, then quit).</summary>
    private static (bool Demo, string? Shots)? DemoArgs(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        var shots = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
        return shots is not null || args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) ? (true, shots) : null;
    }

    /// <summary>Demo mode: its own temporary settings, no single-instance lock, tray, agent or network.</summary>
    private async void StartDemo(string? shots)
    {
        Demo.Rendering = shots is not null;
        _main = new MainWindow(Demo.Start());
        _main.Closed += (_, _) => Shutdown();
        if (shots is null)
        {
            _main.Show();
            return;
        }
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            Shutdown(1);
        };
        try
        {
            await Screenshots.RunAsync(_main, shots, TrayMenu);
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Directory.CreateDirectory(shots);
            File.WriteAllText(Path.Combine(shots, "error.txt"), error.ToString());
            Shutdown(1);
        }
    }

    /// <summary>"--pause" (or "--paused") = true, "--resume" = false, neither = null.</summary>
    private static bool? PauseArg(string[] args) =>
        args.Any(a => a.Equals("--pause", StringComparison.OrdinalIgnoreCase) || a.Equals("--paused", StringComparison.OrdinalIgnoreCase)) ? true
        : args.Any(a => a.Equals("--resume", StringComparison.OrdinalIgnoreCase)) ? false
        : null;

    /// <summary>"--swap next|previous|home|1…10": a window-swap action for the running copy (scripts, tests). Null if absent or unknown.</summary>
    private static string? SwapArg(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--swap", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        var v = args[i + 1].ToLowerInvariant();
        if (int.TryParse(v, out var n)) return n is >= 1 and <= Core.Presets.MaxClients ? Core.SwapActions.Client(n) : null;
        return v is Core.SwapActions.Next or Core.SwapActions.Previous or Core.SwapActions.Home ? v : null;
    }

    private static (bool Tray, string? Start) ParseArgs(string[] args)
    {
        var tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var i = Array.FindIndex(args, a => a.Equals("--start", StringComparison.OrdinalIgnoreCase));
        return (tray, i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
    }

    /// <summary>Another start of the app (shortcut, menu, script): follow its arguments, or just show the window.</summary>
    private void OnSecondInstance(string[] args)
    {
        var (tray, start) = ParseArgs(args);
        if (SwapArg(args) is { } swap && _main is not null)
        {
            _main.ViewModel.RunSwapAction(swap);   // the same as the hotkey; the window stays as it is
            return;
        }
        if (PauseArg(args) is { } pause && _main is not null)
        {
            _main.ViewModel.Paused = pause;   // a pause/resume request only: the window stays as it is
            return;
        }
        if (!tray) ShowMain();
        if (start is not null) StartProfile(start);
    }

    private void StartProfile(string name)
    {
        if (_main is null) return;
        if (_main.ViewModel.SelectProfile(name))
        {
            _main.ViewModel.Status.Info($"\"{name}\": log in to each client – every window is placed as it opens.");
            _main.ViewModel.StartLayout(name);
        }
        else
        {
            ShowMain();
            _main.ViewModel.Status.Warn($"No layout called \"{name}\".");
        }
    }

    private void CreateTray()
    {
        _tray = new TrayIcon
        {
            Icon = Dialogs.AppIcon,
            ToolTipText = AppFlavor.Name,
            Menu = TrayMenu(),
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>The tray icon's menu (also drawn for the website's screenshots).</summary>
    private NativeMenu TrayMenu()
    {
        var menu = new NativeMenu();
        void Add(string header, Action click)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Add($"Open {AppFlavor.Name}", ShowMain);
        Add("Open missing WoW clients", () => Run(_main!.ViewModel.OpenClientsCommand));
        Add("Re-arrange", () => Run(_main!.ViewModel.ArrangeCommand));
        Add("Identify windows", () => Run(_main!.ViewModel.IdentifyCommand));
        menu.Items.Add(new NativeMenuItemSeparator());
        // Start / Stop placing windows, the same as the button on the main screen.
        var pauseItem = new NativeMenuItem("Start placing windows");
        pauseItem.Click += (_, _) => Run(_main!.ViewModel.PauseCommand);
        menu.Items.Add(pauseItem);
        // Hover to activate (done by WoWBoxer's KWin script; the app sends no input). Ticked = on.
        var hoverItem = new NativeMenuItem("Hover to activate windows") { ToggleType = MenuItemToggleType.CheckBox };
        hoverItem.Click += (_, _) =>
        {
            var hover = _main!.ViewModel.Hover;
            hover.Enabled = !hover.Enabled;
        };
        menu.Items.Add(hoverItem);
        // Window swap (Settings › Advanced): only shown while its hotkeys are on
        var homeItem = new NativeMenuItem("Back to home layout");
        homeItem.Click += (_, _) => _main!.ViewModel.RunSwapAction(Core.SwapActions.Home);
        var swapOffItem = new NativeMenuItem("Disable swap hotkeys");
        swapOffItem.Click += (_, _) => _main!.ViewModel.SwapHotkeysEnabled = false;
        menu.Items.Add(homeItem);
        menu.Items.Add(swapOffItem);
        void UpdateItems()
        {
            if (_main is null) return;
            pauseItem.Header = _main.ViewModel.Paused ? "Start placing windows" : "Stop placing windows";
            _main.ViewModel.Hover.Refresh();
            hoverItem.IsChecked = _main.ViewModel.Hover.Enabled;
            homeItem.IsVisible = swapOffItem.IsVisible = _main.ViewModel.SwapHotkeysEnabled;
            homeItem.IsEnabled = _main.ViewModel.IsSwapped;
        }
        menu.Opening += (_, _) => UpdateItems();
        if (_main is not null)
        {
            UpdateItems();
            // a desktop's tray may draw the menu without asking first: keep it current
            _main.ViewModel.PropertyChanged += (_, _) => UpdateItems();
            _main.ViewModel.Hover.PropertyChanged += (_, _) => UpdateItems();
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Report a problem…", () =>
        {
            ShowMain();
            _main!.ViewModel.SelectedTab = AppTab.Help;
            Run(_main.ViewModel.ReportProblemCommand);
        });
        Add("Quit", () => Shutdown());
        return menu;
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();   // our own window, opened by the user: fine to activate
    }

    private void Shutdown(int code = 0) => _desktop?.Shutdown(code);

    /// <summary>The process keeps running until the UI thread has quit (and run <see cref="OnExit"/>).</summary>
    private void QuitOnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Dispatcher.UIThread.Post(() => Shutdown());
    }

    private void OnExit()
    {
        _main?.ViewModel.Hover.OnAppExit();   // hover off on quit (unless Settings keeps it for the next start)
        _main?.ViewModel.DisposeHotkeys();
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }
        // the agent stops keeping windows above, the swap shortcuts are cleared and the script is unloaded (on a pool
        // thread: the UI thread waits here, so the D-Bus answers must not need it)
        if (Link == AgentLinkStart.Owner) Task.Run(AgentLink.StopAsync).Wait(TimeSpan.FromSeconds(3));
        Demo.End();
        _sigterm?.Dispose();
        _sigint?.Dispose();
    }
}
