using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using WoWBoxer.Layouts.App.Infrastructure;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>What the open WoW window in a slot really is right now: its game area, the action bar's effective aspect
/// (aspect / UI Scale) and whether KWin has it "keep above".</summary>
public sealed record LiveSlot(int Width, int Height, double Effective, bool Topmost);

/// <summary>What the view model needs from the window (dialogs, clipboard, overlays).</summary>
public interface IUserDialogs
{
    string? Prompt(string title, string message, string initial);
    bool Confirm(string title, string message);
    /// <summary>A question with several answers; returns the index of the button, or -1 when the dialog was closed (Esc).</summary>
    int Choose(string title, string message, IReadOnlyList<string> buttons);
    /// <summary>The import dialog: returns the code when it is valid and the user pressed OK (live check with <paramref name="check"/>).</summary>
    string? AskShareCode(Func<string, (bool Ok, string Message)> check);
    string? OpenFile(string filter);
    string? SaveFile(string filter, string fileName);
    void CopyToClipboard(string text);
    void ShowIdentify(IReadOnlyList<(PixelRect Rect, string Number, string Caption)> labels);
}

/// <summary>
/// The whole app's state. Start/Stop: while started, the app watches for WoW windows and puts each new one in the next
/// free slot. It opens stopped (nothing is touched) unless a shortcut, "--resume" or the Settings switch starts it. Flow: how many clients → pick a layout → pick the monitors → fine-tune on the canvas; the one primary
/// button opens the missing clients or re-arranges. The current layout is always a saved profile (auto-saved); every
/// edit can be undone and moves the open WoW windows right away.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string WebsiteUrl = "https://wowboxer.com";
    public const string WebsiteGuideUrl = WebsiteUrl + "/guides/getting-started/";

    private readonly ProfileStore _store = new(AppSettings.ProfilesFolder);
    private readonly AppSettings _settings;
    private readonly IUserDialogs _dialogs;
    private readonly Stack<UndoStep> _undo = new();

    /// <summary>One undo step: the layout, and for squad reorders also which window and WoW account were in which row.</summary>
    private sealed record UndoStep(LayoutProfile Profile, Dictionary<nint, int>? WindowSlots = null, List<string>? Accounts = null);
    private readonly SemaphoreSlim _liveLock = new(1, 1);
    private ProfileItem? _selectedProfile;
    private LayoutProfile? _current;
    private int _selectedSlotIndex = -1;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private CancellationTokenSource? _live;
    private bool _busy;
    private AppTab _tab;
    private StatusLine? _lastStatus;
    private bool _loaded;   // opening the app never moves windows; only edits do
    private readonly ClientLauncher _launcher = new();
    private readonly SlotKeeper _keeper = new();   // which open window sits in which slot (always watching)
    private bool _paused, _stepping;
    private DateTime _openingUntil;                // after "Open N WoW clients": the button says "Awaiting clients" until then

    /// <param name="paused">Open stopped: no window is placed until Start.</param>
    public MainViewModel(AppSettings settings, IUserDialogs dialogs, bool paused = false)
    {
        _paused = paused;
        _settings = settings;
        _dialogs = dialogs;
        WinePrefixes.ProtonPathSetting = settings.ProtonPath;
        _tab = Enum.TryParse<AppTab>(settings.LastTab, out var tab) ? tab : AppTab.Setup;
        Hover = new HoverViewModel(settings, Status, OpenSwitchingGuide, OpenUrl);
        ReportProblemCommand = new RelayCommand(() => ReportRequested?.Invoke());
        Guide = new GuideViewModel(Hover, new RelayCommand(() => SelectedTab = AppTab.Help), new RelayCommand(() => SelectedTab = AppTab.Settings));

        SetTabCommand = new RelayCommand(p => SelectedTab = Enum.Parse<AppTab>(p!.ToString()!));
        MoreClientsCommand = new RelayCommand(() => ClientCount++, () => ClientCount < Presets.MaxClients);
        FewerClientsCommand = new RelayCommand(() => ClientCount--, () => ClientCount > Presets.MinClients);
        ResetCommand = new RelayCommand(ResetToPreset, () => Current is not null);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        SaveAsCommand = new RelayCommand(SaveAs, () => Current is not null);
        RenameCommand = new RelayCommand(Rename, () => _selectedProfile is not null);
        DeleteCommand = new RelayCommand(Delete, () => _selectedProfile is not null && Profiles.Count > 1);
        CopyCodeCommand = new RelayCommand(CopyCode, () => Current is not null);
        ImportCodeCommand = new RelayCommand(ImportCode);
        ImportFileCommand = new RelayCommand(ImportFile);
        ExportFileCommand = new RelayCommand(ExportFile, () => Current is not null);
        CaptureCommand = new RelayCommand(Capture);
        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        IdentifyCommand = new RelayCommand(Identify);
        ArrangeCommand = new RelayCommand(() => _ = ArrangeAsync(), () => !_busy && Current is not null && Windows.Count > 0);
        PrimaryCommand = new RelayCommand(Primary, () => !_busy && Current is not null && (_paused || !Opening));
        OpenClientsCommand = new RelayCommand(OpenClients, () => !_busy && Current is not null && Missing > 0);
        OpenBattleNetCommand = new RelayCommand(OpenBattleNet);
        IncludeCommand = new RelayCommand(Include);
        PauseCommand = new RelayCommand(() => Paused = !Paused);
        StartCommand = new RelayCommand(() => Paused = false, () => _paused);
        StopCommand = new RelayCommand(() => Paused = true, () => !_paused);
        RestoreFramesCommand = new RelayCommand(RestoreFrames);
        OpenLogsCommand = new RelayCommand(() => Open(StatusLog.LogFolder));
        OpenLayoutsFolderCommand = new RelayCommand(() => Open(AppSettings.ProfilesFolder));
        ExperimentalSwitches = [.. ExperimentalFeatures.All.Select(f => new ExperimentalSwitch(f, settings, Status))];
        DiagnoseCommand = new RelayCommand(() => _ = DiagnoseAsync(), () => !_busy);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        BrowseBattleNetCommand = new RelayCommand(BrowseBattleNet);
        ShowFirstRunCommand = new RelayCommand(() => FirstRunRequested?.Invoke());
        OpenDiscordCommand = new RelayCommand(() => OpenUrl(Community.Links.Discord));
        OpenYouTubeCommand = new RelayCommand(() => OpenUrl(Community.Links.YouTube));
        OpenWebsiteCommand = new RelayCommand(() => OpenUrl(Community.Links.Website));

        Status.Lines.CollectionChanged += (_, _) => LastStatus = Status.Lines.LastOrDefault();
        Layouts =
        [
            Card(PresetKind.Side, "Main + side", "Main big, followers stacked beside it. 2+ monitors: the main fills monitor 1, followers go on the others. 6–10 clients: one block per party."),
            Card(PresetKind.Overlay, "Overlays", "Main fills the screen; followers float in a row along the bottom – one row per party."),
            Card(PresetKind.Grid, "Grid", "Every client the same size, spread evenly over your monitors."),
            Card(PresetKind.Custom, "Custom", "Your own arrangement: keeps what is on the map – drag, resize and line up windows anywhere, across monitors too. Saved as a draft on every change."),
        ];
        FlipSplitCommand = new RelayCommand(FlipSplit, () => HasSplit);
        _monitorCheck.Tick += (_, _) =>
        {
            _monitorCheck.Stop();
            CheckScreens();
        };

        CloseMineCommand = new RelayCommand(() => _launcher.CloseMine(Status), () => _launcher.Started.Count > 0);
        RefreshHintsCommand = new RelayCommand(() =>
        {
            ReadWowAccounts();
            Status.Info($"Read {WowAccounts.Count} WoW account(s) and {WowAccounts.Sum(a => a.Characters.Count)} character(s) from the WTF folder.");
        });
        BrowseGameCommand = new RelayCommand(BrowseGame);
        FitActionBarCommand = new RelayCommand(FitActionBar, () => HasSlot);
        FixUiScaleCommand = new RelayCommand(FixUiScale, () => Current is not null);
        ResetUiScaleCommand = new RelayCommand(ResetUiScale, () => UiScaleNow.Use);

        RefreshMonitors();
        LoadProfiles();
        RefreshWindows();
        ReadWowAccounts();
        ApplyOnTop();
        // Keeps "on top" right: KWin's script puts keep-above back when Wine clears it (listening only).
        _guard = new ZOrderGuard(WantedZOrder, OnZCorrected);
        _lastRunning = RunningClientsNow();
        _loaded = true;
        ApplyHotkeys();   // window swap (Settings › Advanced): registers its hotkeys only when switched on
        if (_lastRunning == 0) AutoFitUiScale("no clients are running");
        // The agent's news arrives on a D-Bus thread: everything is handled on the UI thread.
        AgentLink.HandEdited += (h, frame) => Dispatcher.UIThread.Post(() => OnHandEdited(h, frame));
        AgentLink.KeepAboveGaveUp += h => Dispatcher.UIThread.Post(() => OnKeepAboveGaveUp(h));
        AgentLink.StateChanged += OnAgentState;
    }

    private int _monitorsQueued;

    /// <summary>A report from the agent (outputs may have changed): re-read the monitors once per burst of reports.</summary>
    private void OnAgentState()
    {
        if (Interlocked.Exchange(ref _monitorsQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _monitorsQueued, 0);
            RefreshMonitors();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// A squad window moved or resized by hand (KWin) while placing is started: the same as a drag on the map – a preset
    /// becomes Custom from exactly that, and Ctrl+Z undoes it. While stopped, windows are left alone and so is the layout.
    /// </summary>
    private void OnHandEdited(nint handle, PixelRect frame)
    {
        if (_paused || Busy || Current is null || Monitors.Count == 0 || Placed is not { } placed) return;
        if (Windows.FirstOrDefault(w => w.Window.Handle == handle) is not { } item || item.Slot - 1 >= placed.Slots.Count) return;
        var index = item.Slot - 1;
        var screens = ChosenScreens.Count > 0 ? ChosenScreens : Monitors;
        OnSlotEdited(index, SlotEditing.WithRect(placed.Slots[index], frame, screens, Current.Options) with { Fit = FitMode.None });
        Status.Info($"Window {item.Slot} was moved by hand – the layout follows it (Ctrl+Z undoes it).");
    }

    private void OnKeepAboveGaveUp(nint handle)
    {
        var slot = Windows.FirstOrDefault(w => w.Window.Handle == handle)?.Slot ?? 0;
        Status.Warn($"Window {slot}: Wine keeps clearing \"on top\" – WoWBoxer's KWin script stopped putting it back. Re-arrange tries again.");
    }

    private readonly ZOrderGuard? _guard;
    private readonly Dictionary<nint, DateTime> _zLogged = [];
    private int _lastRunning;

    /// <summary>The open WoW windows with the z-order of the slot each one is in. Nothing while paused.</summary>
    private IReadOnlyList<(nint Handle, ZOrder Z)> WantedZOrder()
    {
        if (Placed is not { } placed || _paused) return [];
        return Windows.Where(w => w.Slot - 1 < placed.Slots.Count).Select(w => (w.Window.Handle, placed.Slots[w.Slot - 1].Z)).ToList();
    }

    private void OnZCorrected(nint handle, ZOrder z)
    {
        var slot = Windows.FirstOrDefault(w => w.Window.Handle == handle)?.Slot ?? 0;
        if (_zLogged.TryGetValue(handle, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1)) return;   // log once a minute per window
        _zLogged[handle] = DateTime.UtcNow;
        Status.Info($"Window {slot}: \"on top\" was {(z == ZOrder.Top ? "lost" : "set by something else")} – put right again.");
    }

    /// <summary>How many clients of the squad's install are running (Auto-fit may only write while this is 0).</summary>
    private int RunningClientsNow() =>
        SquadExe is { } exe ? UiScaleFix.RunningClients(Path.GetDirectoryName(exe)!) : -1;

    // =====================================================================================
    // State
    // =====================================================================================

    public StatusLog Status { get; } = new();
    public GuideViewModel Guide { get; }
    public HoverViewModel Hover { get; }
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<GameWindowItem> Windows { get; } = [];
    public ObservableCollection<PartyMember> Party { get; } = [];
    /// <summary>The layout cards, each with a live mini preview for the current clients and monitors.</summary>
    public IReadOnlyList<LayoutCard> Layouts { get; }

    private LayoutCard Card(PresetKind kind, string title, string blurb) => new(kind, title, blurb) { Apply = new RelayCommand(() => PickLayout(kind)) };

    /// <summary>A layout card: Custom keeps what is on the map; any other card asks first when leaving unsaved Custom changes.</summary>
    private void PickLayout(PresetKind kind)
    {
        if (Current is null || kind == Current.Preset) return;
        if (kind == PresetKind.Custom)
        {
            ChooseCustom();   // keeps what is on the map – never fixed positions
            return;
        }
        LeaveCustom(switchingLayout: false);
        ApplyKind(kind);
    }
    public IReadOnlyList<int> ClientCounts { get; } = Enumerable.Range(Presets.MinClients, Presets.MaxClients - Presets.MinClients + 1).ToList();
    public IReadOnlyList<WowClass> Classes { get; } = [WowClasses.Automatic, .. WowClasses.All];
    public IReadOnlyList<AspectChoice> AspectChoices { get; } =
    [
        new(4.0 / 3, "4:3 – full Classic action bar"),
        new(1.25, "5:4 – a little narrower allowed"),
        new(0, "Off – use the window as drawn"),
    ];

    public event Action? FirstRunRequested;

    /// <summary>"Report a problem" (Guide, Settings, tray): the window shows the report from <see cref="BuildReport"/>.</summary>
    public event Action? ReportRequested;

    /// <summary>The full version, pre-release part included ("0.7.0-beta.1"), without the build's commit id.</summary>
    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public string Version => AppVersion;

    public string WindowTitle => $"{AppFlavor.Name} {Version}";

    /// <summary>A local dev build (tools/dev-build.ps1): DEV badge in the title bar.</summary>
    public bool IsDevBuild => AppFlavor.IsDev;

    public AppTab SelectedTab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            _settings.LastTab = value.ToString();
            _settings.Save();
        }
    }

    public StatusLine? LastStatus { get => _lastStatus; private set => Set(ref _lastStatus, value); }

    public IReadOnlyList<MonitorInfo> Monitors { get => _monitors; private set => Set(ref _monitors, value); }

    public string MonitorSummary => string.Join("    ", Monitors.Select(m => $"Monitor {m.Index}: {m.Bounds.Width}×{m.Bounds.Height} · {m.ScalePercent} %"));

    public LayoutProfile? Current
    {
        get => _current;
        private set
        {
            if (!Set(ref _current, value)) return;
            SyncSwapToLayout();
            foreach (var name in new[] { nameof(Borderless), nameof(CoverTaskbar), nameof(SelectedAspect), nameof(ClientCount), nameof(LayoutTitle), nameof(SavedText) })
                OnPropertyChanged(name);
            RefreshScreens();
            if ((value?.Slots.Count ?? 0) != _slotChoices.Count)
            {
                OnPropertyChanged(nameof(SlotChoices));
                RefreshWindows();   // fewer slots free windows, more slots take waiting ones
            }
            RefreshCards();
            RefreshParty();
            RaiseSlotProperties();
        }
    }

    public string LayoutTitle => Current is null ? "No layout" : Current.Name;
    public string SavedText => CustomSavedText;

    public ProfileItem? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || value == _selectedProfile) return;
            LeaveCustom(switchingLayout: true);   // asks only for an unsaved custom arrangement
            if (!Set(ref _selectedProfile, value)) return;
            _undo.Clear();
            _redo.Clear();
            MonitorNotice = "";
            Current = value.Profile;
            LoadDraft();
            _selection.Clear();
            RaiseSelection();
            CheckScreens();   // its monitors may be numbered differently now, or not connected
            SelectedSlotIndex = 0;
            LastProfileName = value.Name;
            ScheduleLive();
        }
    }

    public int SelectedSlotIndex
    {
        get => _selectedSlotIndex;
        set
        {
            if (!Set(ref _selectedSlotIndex, value)) return;
            if (value != _expandedIndex) ExpandedIndex = -1;   // selecting another row closes the open details
            RaiseSlotProperties();
        }
    }

    private int _expandedIndex = -1;

    /// <summary>The squad row whose details are open (-1 = none). Its window is the selected one.</summary>
    public int ExpandedIndex
    {
        get => _expandedIndex;
        set
        {
            if (!Set(ref _expandedIndex, value)) return;
            for (var i = 0; i < Party.Count; i++) Party[i].IsExpanded = i == value;
        }
    }

    private void ToggleDetails(int index)
    {
        if (ExpandedIndex == index)
        {
            ExpandedIndex = -1;
            return;
        }
        SelectedSlotIndex = index;
        ExpandedIndex = index;
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Stopped (Start/Stop button, tray): no window is moved or re-stacked until Start.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (!Set(ref _paused, value)) return;
            if (value)
            {
                _live?.Cancel();
                Status.Info("Stopped: WoW windows are left alone until you press Start.");
            }
            else
            {
                _keeper.Settle();   // windows that opened or moved meanwhile go to their slots now
                ApplyOnTop();
                Status.Info("Started: every WoW window of the squad goes to its slot.");
            }
            RaiseSquadStatus();
        }
    }

    // =====================================================================================
    // Step 1 + 2: clients and layout
    // =====================================================================================

    /// <summary>How many clients. Changing it re-applies the current layout type for the new number.</summary>
    public int ClientCount
    {
        get => Current?.Clients ?? 3;
        set
        {
            value = Math.Clamp(value, Presets.MinClients, Presets.MaxClients);
            if (Current is null || value == Current.Clients) return;
            ApplyKind(Current.Preset ?? PresetKind.Side, value);
        }
    }

    /// <summary>The current layout's card (none for a captured or imported layout).</summary>
    public LayoutCard? SelectedLayout
    {
        get => Layouts.FirstOrDefault(c => c.Kind == Current?.Preset);
        set
        {
            if (value is not null) PickLayout(value.Kind);
        }
    }

    private void RefreshCards()
    {
        var n = Current?.Clients ?? 3;
        foreach (var card in Layouts)
        {
            card.IsSelected = Current?.Preset == card.Kind;
            card.Monitors = Monitors;
            card.Screens = ChosenScreens;
            // the Custom card shows what choosing it gives: this layout's draft, or the arrangement on the map now
            card.Preview = card.Kind == PresetKind.Custom && Current is not null
                ? (Current.Preset == PresetKind.Custom ? Current : _draft is { } d && d.Custom.Slots.Count == Current.Slots.Count ? d.Custom : Current)
                : TryPreset(card.Kind, n, out var preview, quiet: true) ? preview : null;
        }
        OnPropertyChanged(nameof(SelectedLayout));
    }

    /// <summary>
    /// Applies a layout type, client count or monitor choice (anything not given stays as it is), keeping names and
    /// classes. Undo brings the old one back.
    /// </summary>
    private void ApplyKind(PresetKind kind, int? clients = null, IReadOnlyList<MonitorInfo>? screens = null, PartySplit? split = null, bool? followersOnSecond = null)
    {
        if (Current is null || !TryPreset(kind, clients ?? Current.Clients, out var preset, quiet: false, screens, split, followersOnSecond)) return;
        ResetSwap(quiet: true);   // a new layout starts at home
        var slots = preset.Slots.Select((s, i) => i < Current.Slots.Count ? s with { Name = Current.Slots[i].Name, Class = Current.Slots[i].Class, CombatRole = Current.Slots[i].CombatRole } : s).ToList();
        // Custom with another number of clients: the windows that exist keep their places
        var keepPlaces = kind == PresetKind.Custom && Current.Preset == PresetKind.Custom && screens is null;
        if (keepPlaces) slots = [.. CustomLayouts.WithCount(Current.Slots, preset)];
        // a new preset brings its own main slot (the large window); Custom keeping its places keeps its crowns
        var mains = keepPlaces ? Current.SwapMainSlots : null;
        Update(Current with { Clients = preset.Clients, Preset = kind, Slots = slots, Screens = preset.Screens, Split = preset.Split, FollowersOnSecond = preset.FollowersOnSecond, SwapMainSlots = mains });
        SelectedSlotIndex = 0;
    }

    private void ResetToPreset()
    {
        if (Current is null) return;
        if (Current.Preset is PresetKind.Custom or null)
        {
            // Custom has no fixed positions: back to the layout it was made from (or Main + side)
            if (_draft?.Before is { } before)
            {
                Update(before with { Name = Current.Name });
                Status.Info($"Back to {Presets.DisplayName(before.Preset ?? PresetKind.Side)} as it was before Custom. Your custom arrangement stays as a draft; Ctrl+Z brings it back.");
            }
            else
            {
                ApplyKind(PresetKind.Side);
                Status.Info("Reset to Main + side. Ctrl+Z brings your custom arrangement back.");
            }
            return;
        }
        ApplyKind(Current.Preset.Value);
        Status.Info($"Reset to {Presets.DisplayName(Current.Preset.Value)}. Ctrl+Z brings your changes back.");
    }

    /// <summary>The preset for the chosen monitors (or <paramref name="screens"/>); split and overlay rows as the current layout has them.</summary>
    private bool TryPreset(PresetKind kind, int clients, out LayoutProfile profile, bool quiet, IReadOnlyList<MonitorInfo>? screens = null,
        PartySplit? split = null, bool? followersOnSecond = null, LayoutOptions? options = null)
    {
        try
        {
            options ??= Current?.Options ?? new LayoutOptions { MinAspect = 4.0 / 3 };
            screens ??= ChosenScreens.Count > 0 ? ChosenScreens : [Core.Monitors.Primary(Monitors)];
            profile = Presets.ForScreens(kind, clients, screens, options with { Fit = FitMode.ShrinkAnchor },
                split ?? Current?.Split, followersOnSecond ?? Current?.FollowersOnSecond ?? false);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            if (!quiet) Status.Warn(e.Message.Split(" (Parameter")[0]);
            profile = null!;
            return false;
        }
    }

    // =====================================================================================
    // Window rules
    // =====================================================================================

    /// <summary>
    /// Remove title bars (Setup and Settings › Window rules; contributed by Limerobin, PR #1). A layout that is still its
    /// preset is rebuilt for the new frame size, so Main + side followers fill their column; a layout whose windows were
    /// moved by hand keeps them (only the option changes). Open windows get the new frame at once – unless the app is
    /// stopped: then nothing is touched until Start.
    /// </summary>
    public bool Borderless
    {
        get => Current?.Options.Borderless ?? false;
        set
        {
            if (Current is null || Current.Options.Borderless == value) return;
            ResetSwap(quiet: true);   // the layout changes at home
            var options = Current.Options with { Borderless = value };
            if (Current.Preset is { } kind && IsUntouchedPreset(kind) && TryPreset(kind, Current.Clients, out var preset, quiet: false, options: options))
            {
                var slots = preset.Slots
                    .Select((s, i) => i < Current.Slots.Count ? s with { Name = Current.Slots[i].Name, Class = Current.Slots[i].Class, CombatRole = Current.Slots[i].CombatRole } : s)
                    .ToList();
                Update(Current with { Options = options, Slots = slots, Screens = preset.Screens, Split = preset.Split, FollowersOnSecond = preset.FollowersOnSecond });
            }
            else
                EditOptions(_ => options);
            if (_paused)
                Status.Info(value ? "Remove title bars: on. The windows change when you press Start (stopped = every window is left alone)."
                    : "Remove title bars: off. The windows get their title bars back when you press Start.");
            else
                ApplyBorderlessChrome();
            OnPropertyChanged();
        }
    }

    /// <summary>True when every slot still sits exactly where the preset puts it (nothing dragged or resized by hand).</summary>
    private bool IsUntouchedPreset(PresetKind kind)
    {
        if (Current is null || !TryPreset(kind, Current.Clients, out var preset, quiet: true)) return false;
        return preset.Slots.Count == Current.Slots.Count && preset.Slots.Zip(Current.Slots).All(p =>
            p.First.Monitor == p.Second.Monitor && Math.Abs(p.First.X - p.Second.X) < 0.001 && Math.Abs(p.First.Y - p.Second.Y) < 0.001
            && Math.Abs(p.First.W - p.Second.W) < 0.001 && Math.Abs(p.First.H - p.Second.H) < 0.001 && p.First.Z == p.Second.Z
            && p.First.Anchor == p.Second.Anchor && p.First.Fit == p.Second.Fit);
    }

    /// <summary>Use the whole monitor, including the taskbar's area.</summary>
    public bool CoverTaskbar
    {
        get => Current?.Options.UseTaskbarArea ?? false;
        set
        {
            EditOptions(o => o with { UseTaskbarArea = value });
            OnPropertyChanged(nameof(TaskbarHint));
        }
    }

    public string TaskbarHint => Taskbar.AutoHides()
        ? "Your taskbar hides itself, so covering its area gives the windows the full screen."
        : CoverTaskbar
            ? "Your panel is always shown and stays on top: it will cover the bottom of the windows. Set the panel to auto-hide (right-click it › Show Panel Configuration › Visibility), or switch this off."
            : "The strip at the bottom of each monitor is your taskbar. Windows stay above it.";

    public AspectChoice? SelectedAspect
    {
        get => Current is null ? null : AspectChoices.MinBy(c => Math.Abs(c.Value - Current.Options.MinAspect));
        set
        {
            if (value is not null) EditOptions(o => o with { MinAspect = value.Value });
        }
    }

    // =====================================================================================
    // Selected window (inspector)
    // =====================================================================================

    private LayoutSlot? Slot => Placed is { } placed && SelectedSlotIndex >= 0 && SelectedSlotIndex < placed.Slots.Count ? placed.Slots[SelectedSlotIndex] : null;

    private PixelRect SlotRect => Slot is { } s && Monitors.Count > 0
        ? SlotEditing.Raw(s, Core.Monitors.Find(Monitors, s.Monitor) ?? Core.Monitors.Primary(Monitors), Current!.Options)
        : default;

    public bool HasSlot => Slot is not null;

    /// <summary>The squad row of the selected window (its account and Identify, shown in the open details).</summary>
    public PartyMember? SelectedMember => Party.ElementAtOrDefault(SelectedSlotIndex);

    /// <summary>
    /// "Window 1" … for the inspector's window picker (keyboard and UI Automation friendly). A new list only when the
    /// count changes: a new list would make the picker drop its selection on every edit.
    /// </summary>
    public IReadOnlyList<string> SlotChoices
    {
        get
        {
            var n = Current?.Slots.Count ?? 0;
            if (_slotChoices.Count != n) _slotChoices = Enumerable.Range(1, n).Select(i => $"Window {i}").ToList();
            return _slotChoices;
        }
    }

    private IReadOnlyList<string> _slotChoices = [];
    public string SlotHeader => Slot is { } s ? $"Window {s.N}{(s.Role == SlotRole.Main ? " · main" : "")}" : "Click a window";
    public IBrush SlotBrush => Slot is { } s ? new ImmutableSolidColorBrush(WowClasses.ColorFor(s.Class)) : Brushes.Gray;

    public string SlotName
    {
        get => Slot?.Name ?? "";
        set
        {
            MarkSlotEdited(SelectedSlotIndex);
            EditSlot(s => s with { Name = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        }
    }

    public WowClass SlotClass
    {
        get => WowClasses.Find(Slot?.Class) ?? WowClasses.Automatic;
        set
        {
            MarkSlotEdited(SelectedSlotIndex);
            EditSlot(s => s with { Class = value is null || value == WowClasses.Automatic ? null : value.Id });
        }
    }

    public int SlotX { get => SlotRect.X; set => EditSlotRect(SlotRect with { X = value }); }
    public int SlotY { get => SlotRect.Y; set => EditSlotRect(SlotRect with { Y = value }); }
    public int SlotW { get => SlotRect.Width; set => EditSlotRect(SlotRect with { Width = Math.Max(value, SlotEditing.MinSlotSize) }); }
    public int SlotH { get => SlotRect.Height; set => EditSlotRect(SlotRect with { Height = Math.Max(value, SlotEditing.MinSlotSize) }); }

    public bool SlotOnTop
    {
        get => Slot?.Z == ZOrder.Top;
        set => EditSlot(s => s with { Z = value ? ZOrder.Top : (s.Role == SlotRole.Main ? ZOrder.Bottom : ZOrder.Normal) });
    }

    public bool SlotKeepSize
    {
        get => Slot?.Fit == FitMode.None;
        set => EditSlot(s => s with { Fit = value ? FitMode.None : null });
    }

    public string SlotInfo
    {
        get
        {
            if (Slot is not { } s || Current is null) return "";
            var raw = SlotRect;
            var monitor = Core.Monitors.Find(Monitors, s.Monitor) ?? Core.Monitors.Primary(Monitors);
            var frame = LayoutEngine.FrameSize(Current.Options, monitor.Dpi);
            var fitted = LayoutEngine.Fit(raw, s.Fit ?? Current.Options.Fit, s.Anchor, Current.Options.MinAspect, frame);
            var (gw, gh) = LayoutEngine.GameArea(fitted, frame);
            var aspect = gh == 0 ? 0 : (double)gw / gh;
            var number = ChosenScreens.ToList().FindIndex(m => m.Index == monitor.Index) + 1;
            var text = $"Monitor {(number > 0 ? number : monitor.Index)} · window {fitted.Width} × {fitted.Height} · game draws {gw} × {gh} ({aspect:0.00})";
            if (fitted != raw) text += $"\nShortened from {raw.Width} × {raw.Height} to keep the aspect rule.";
            return text;
        }
    }

    /// <summary>The game-area aspect a slot of the layout gives (title bar excluded).</summary>
    private double PlannedAspect(int index)
    {
        if (Current is null || Monitors.Count == 0 || index < 0 || index >= Current.Slots.Count) return 0;
        var s = Current.Slots[index];
        var monitor = Core.Monitors.Find(Monitors, s.Monitor) ?? Core.Monitors.Primary(Monitors);
        var frame = LayoutEngine.FrameSize(Current.Options, monitor.Dpi);
        var (w, h) = LayoutEngine.GameArea(LayoutEngine.Fit(SlotEditing.Raw(s, monitor, Current.Options), s.Fit ?? Current.Options.Fit, s.Anchor, Current.Options.MinAspect, frame), frame);
        return h == 0 ? 0 : (double)w / h;
    }

    private double SlotGameAspect => PlannedAspect(SelectedSlotIndex);

    /// <summary>
    /// The real action-bar state of the selected window when its WoW window is open: measured from the size the game
    /// really draws at (the same picture "wbx snap" saves) and the UI Scale in Config.wtf. Otherwise the layout's plan.
    /// </summary>
    public string SlotBarText
    {
        get
        {
            if (LiveSlots.TryGetValue(SelectedSlotIndex, out var m))
                return $"Now: game draws {m.Width} × {m.Height}, UI Scale {UiScaleNow.Display} · {ActionBar.Verdict(m.Effective)}";
            var planned = UiScale.EffectiveAspect(SlotGameAspect, UiScaleNow);
            return $"Planned: aspect {SlotGameAspect:0.00}, UI Scale {UiScaleNow.Display} · {ActionBar.Verdict(planned)}";
        }
    }

    private double SlotEffectiveAspect => LiveSlots.TryGetValue(SelectedSlotIndex, out var m) ? m.Effective : UiScale.EffectiveAspect(SlotGameAspect, UiScaleNow);

    /// <summary>The fix for a clipped bar: the UI Scale this window needs (one value is shared by every client).</summary>
    public string SlotUiScaleHint => SlotEffectiveAspect >= 4.0 / 3 - 0.001 || SlotGameAspect <= 0
        ? ""
        : _settings.AutoFitActionBar && SquadGame == WowGame.ClassicEra
            ? $"Auto-fit sets UI Scale {UiScale.ForSquad(Enumerable.Range(0, Current?.Slots.Count ?? 0).Select(PlannedAspect)):0.00} the next time all clients are closed."
            : $"Fix: UI Scale {UiScale.ForFullBar(SlotGameAspect):0.00} or lower (Settings › Action bar), or shorten this window.";

    /// <summary>WoW's UI Scale for the selected game, read from the shared Config.wtf (read-only).</summary>
    public UiScaleSetting UiScaleNow { get; private set; } = UiScaleSetting.Automatic;

    public double UiScaleEffective => UiScaleNow.Effective;

    /// <summary>Slot index → what the open window in that slot really shows (game area and the bar's effective aspect).</summary>
    public IReadOnlyDictionary<int, LiveSlot> LiveSlots { get; private set; } = new Dictionary<int, LiveSlot>();

    /// <summary>Re-reads UI Scale and measures every open window in its slot. Raises only when something changed.</summary>
    private void MeasureBars()
    {
        var exe = SquadExe;
        var scale = exe is null ? UiScaleSetting.Automatic : UiScaleFix.Read(exe);
        var measured = new Dictionary<int, LiveSlot>();
        foreach (var item in Windows)
        {
            var i = item.Slot - 1;
            if (Current is null || i >= Current.Slots.Count) continue;
            var (w, h) = WindowFinder.ClientSizeOf(item.Window.Handle);
            if (w > 0 && h > 0) measured[i] = new LiveSlot(w, h, UiScale.EffectiveAspect((double)w / h, scale), WindowPlacer.IsTopmost(item.Window.Handle));
        }
        if (scale == UiScaleNow && measured.Count == LiveSlots.Count && measured.All(kv => LiveSlots.TryGetValue(kv.Key, out var old) && old == kv.Value)) return;
        UiScaleNow = scale;
        LiveSlots = measured;
        OnPropertyChanged(nameof(UiScaleNow));
        OnPropertyChanged(nameof(UiScaleEffective));
        OnPropertyChanged(nameof(LiveSlots));
        OnPropertyChanged(nameof(UiScaleText));
        OnPropertyChanged(nameof(LayoutAdvice));
        for (var i = 0; i < Party.Count; i++)
        {
            Party[i].OnTop = Placed?.Slots.ElementAtOrDefault(i)?.Z == ZOrder.Top;
            Party[i].TopNow = measured.TryGetValue(i, out var live) ? live.Topmost : null;
        }
        RaiseSlotProperties();
    }

    /// <summary>
    /// "Fix action bar": the one UI Scale at which every window of the layout shows the whole bar. Written to the
    /// install's Config.wtf only while all its clients are closed; otherwise the exact in-game steps are shown.
    /// </summary>
    private void FixUiScale()
    {
        if (Current is null) return;
        var need = UiScale.ForSquad(Enumerable.Range(0, Current.Slots.Count).Select(PlannedAspect));
        if (need is null)
        {
            Status.Ok("Every window of this layout already shows the whole action bar – no UI Scale needed.");
            return;
        }
        if (SquadExe is not { } exe)
        {
            Status.Warn("The WoW client was not found. Set it in Settings › WoW client.");
            return;
        }
        if (UiScaleNow.Use && UiScaleNow.Scale <= need.Value + 0.001)
        {
            Status.Ok($"UI Scale is already {UiScaleNow.Display}: every window shows the whole action bar (from each client's next start).");
            return;
        }
        var running = UiScaleFix.RunningClients(Path.GetDirectoryName(exe)!);
        if (running > 0)
        {
            Status.Warn($"UI Scale {need:0.00} makes the whole action bar fit in every window of \"{Current.Name}\".");
            Status.Info($"Either close all {running} {SquadGameName} client(s) and press Fix action bar again – the app then writes it for you –");
            Status.Info($"or in each client: Options › search \"UI Scale\" › tick Use UI Scale › set {need:0.00}. (One value for all clients; the last client to close saves it.)");
            return;
        }
        if (UiScaleFix.Write(exe, use: true, need.Value) is { } error)
        {
            Status.Warn(error);
            return;
        }
        MeasureBars();
        Status.Ok($"UI Scale set to {need:0.00} in {SquadGameName}'s Config.wtf (a backup was kept). Open your clients: every window shows the whole action bar.");
    }

    /// <summary>Turns "Use UI Scale" off again (automatic UI size), with the same closed-clients rule.</summary>
    private void ResetUiScale()
    {
        if (SquadExe is not { } exe) return;
        if (UiScaleFix.Write(exe, use: false, UiScaleNow.Scale) is { } error)
        {
            Status.Warn(error + " Or untick Use UI Scale in game.");
            return;
        }
        if (_settings.AutoFitActionBar)
        {
            AutoFitActionBar = false;   // or it would set it again the next time the clients are closed
            OnPropertyChanged(nameof(AutoFitActionBar));
        }
        MeasureBars();
        Status.Ok($"Use UI Scale is off again for {SquadGameName} (automatic UI size). Auto-fit action bar is off too.");
    }

    /// <summary>The squad install's UI Scale, for Settings.</summary>
    public string UiScaleText => SquadExe is null
        ? "WoW was not found."
        : $"{SquadGameName}: UI Scale {UiScaleNow.Display}{(SquadGame == WowGame.ClassicEra ? "" : " · Auto-fit runs for Classic Era only (this game's bar is not verified yet)")}";

    /// <summary>Shortens the selected window just enough for a 4:3 game area (keeps its width and its anchor edge).</summary>
    private void FitActionBar()
    {
        if (Slot is not { } s || Current is null) return;
        var monitor = Core.Monitors.Find(Monitors, s.Monitor) ?? Core.Monitors.Primary(Monitors);
        var frame = LayoutEngine.FrameSize(Current.Options, monitor.Dpi);
        var raw = SlotRect;
        var fitted = LayoutEngine.Fit(raw, FitMode.ShrinkAnchor, s.Anchor, 4.0 / 3, frame);
        if (fitted == raw)
        {
            Status.Info($"Window {s.N} already shows the whole action bar.");
            return;
        }
        OnSlotEdited(SelectedSlotIndex, SlotEditing.WithRect(s, fitted, Monitors, Current.Options) with { Fit = FitMode.None });
        Status.Ok($"Window {s.N} is now {fitted.Width} × {fitted.Height}: the whole action bar fits.");
    }
    public IBrush SlotBarBrush => ActionBar.Brush(SlotEffectiveAspect);

    private void RaiseSlotProperties()
    {
        foreach (var name in new[] { nameof(SlotBarText), nameof(SlotBarBrush), nameof(SlotUiScaleHint), nameof(HasSlot), nameof(SelectedMember), nameof(SlotHeader), nameof(SlotBrush), nameof(SlotName), nameof(SlotClass), nameof(SlotX), nameof(SlotY), nameof(SlotW), nameof(SlotH), nameof(SlotOnTop), nameof(SlotKeepSize), nameof(SlotInfo) })
            OnPropertyChanged(name);
    }

    // =====================================================================================
    // Commands
    // =====================================================================================

    public ICommand SetTabCommand { get; }
    public ICommand MoreClientsCommand { get; }
    public ICommand FewerClientsCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand ImportCodeCommand { get; }
    public ICommand ImportFileCommand { get; }
    public ICommand ExportFileCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand RefreshWindowsCommand { get; }
    public ICommand IdentifyCommand { get; }
    public ICommand ArrangeCommand { get; }
    /// <summary>The one button: "Open N WoW clients" while slots are empty, else "Re-arrange".</summary>
    public ICommand PrimaryCommand { get; }
    public ICommand OpenClientsCommand { get; }
    public ICommand OpenBattleNetCommand { get; }
    /// <summary>"Include": other installs' windows join the squad (until the app restarts).</summary>
    public ICommand IncludeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestoreFramesCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenLayoutsFolderCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand BrowseBattleNetCommand { get; }
    public ICommand ShowFirstRunCommand { get; }
    public ICommand CloseMineCommand { get; }
    public ICommand RefreshHintsCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand FitActionBarCommand { get; }
    public ICommand FixUiScaleCommand { get; }
    public ICommand ResetUiScaleCommand { get; }
    public ICommand FlipSplitCommand { get; }
    public ICommand ReportProblemCommand { get; }
    public ICommand OpenDiscordCommand { get; }
    public ICommand OpenYouTubeCommand { get; }
    public ICommand OpenWebsiteCommand { get; }

    // =====================================================================================
    // Saved layouts
    // =====================================================================================

    /// <summary>
    /// Re-reads the monitors (at start, on a display change and before placing). The layout's own monitors are
    /// checked a moment later: monitors blink off and on while they wake up or are re-plugged.
    /// </summary>
    public void RefreshMonitors()
    {
        var monitors = MonitorReader.GetMonitors();
        if (monitors.SequenceEqual(Monitors)) return;
        var first = Monitors.Count == 0;
        Monitors = monitors;
        OnPropertyChanged(nameof(MonitorSummary));
        OnPropertyChanged(nameof(ShowMonitors));
        _monitorCounts = null;
        RefreshScreens();
        RefreshCards();
        RaiseSlotProperties();
        if (!first && Current is not null)
        {
            _monitorCheck.Stop();
            _monitorCheck.Start();
        }
    }

    // =====================================================================================
    // Monitors: which of the connected monitors the layout uses
    // =====================================================================================

    private readonly DispatcherTimer _monitorCheck = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<MonitorInfo> _chosen = [];
    private IReadOnlyList<MonitorChoice>? _monitorCounts;
    private IReadOnlyList<MonitorChoice> _monitorSets = [];
    private string _monitorNotice = "";

    /// <summary>The monitors the layout uses now, in its order (1 = the main's). The map shows exactly these.</summary>
    public IReadOnlyList<MonitorInfo> ChosenScreens => _chosen;

    /// <summary>The Monitors section shows only when more than one monitor is connected.</summary>
    public bool ShowMonitors => Monitors.Count > 1;

    /// <summary>"1 monitor", "2 monitors" … up to the number connected, each with its default monitors.</summary>
    public IReadOnlyList<MonitorChoice> MonitorCounts => _monitorCounts ??= Enumerable.Range(1, Monitors.Count).Select(k =>
    {
        var set = ScreenSet.Choices(Monitors, k)[0];
        return new MonitorChoice(set, k == 1 ? "1 monitor" : $"{k} monitors", ScreenSet.Describe(set, Monitors));
    }).ToList();

    public MonitorChoice? SelectedMonitorCount
    {
        get => MonitorCounts.FirstOrDefault(c => c.Screens.Count == _chosen.Count);
        set
        {
            if (value is not null && value.Screens.Count != _chosen.Count) ApplyScreens(value.Screens);
        }
    }

    /// <summary>Which monitors, when fewer than all are used (every set of that size; the first is the default).</summary>
    public IReadOnlyList<MonitorChoice> MonitorSets => _monitorSets;

    public bool ShowMonitorSets => _monitorSets.Count > 1;

    public MonitorChoice? SelectedMonitorSet
    {
        get => _monitorSets.FirstOrDefault(c => SameScreens(c.Screens, _chosen));
        set
        {
            if (value is not null && !SameScreens(value.Screens, _chosen)) ApplyScreens(value.Screens);
        }
    }

    /// <summary>A short line after the layout lost a monitor ("" = nothing to say).</summary>
    public string MonitorNotice { get => _monitorNotice; private set => Set(ref _monitorNotice, value); }

    private static bool SameScreens(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b) => a.Select(m => m.Index).SequenceEqual(b.Select(m => m.Index));

    private void RefreshScreens()
    {
        IReadOnlyList<MonitorInfo> chosen = Current is null || Monitors.Count == 0 ? [] : ScreenSet.Resolve(Current, Monitors).Chosen;
        if (chosen.Count == 0 && Monitors.Count > 0) chosen = [Core.Monitors.Primary(Monitors)];
        if (chosen.Count != _chosen.Count || !SameScreens(chosen, _chosen) || _monitorCounts is null)
        {
            if (chosen.Count != _chosen.Count || _monitorCounts is null)
                _monitorSets = chosen.Count < Monitors.Count
                    ? ScreenSet.Choices(Monitors, chosen.Count).Select(set => new MonitorChoice(set, ScreenSet.Describe(set, Monitors), "")).ToList()
                    : [];
            _chosen = chosen;
            foreach (var name in new[] { nameof(ChosenScreens), nameof(MonitorCounts), nameof(SelectedMonitorCount), nameof(MonitorSets), nameof(ShowMonitorSets), nameof(SelectedMonitorSet) })
                OnPropertyChanged(name);
        }
        foreach (var name in new[] { nameof(HasSplit), nameof(SplitText), nameof(CanFollowersOnSecond), nameof(FollowersOnSecond), nameof(LayoutAdvice) })
            OnPropertyChanged(name);
    }

    /// <summary>Uses these monitors (a hand-made layout starts again as Custom on them).</summary>
    private void ApplyScreens(IReadOnlyList<MonitorInfo> screens)
    {
        if (Current is null) return;
        MonitorNotice = "";
        if (Current.Preset is null) Status.Info("This layout was made by hand: it starts again as Custom on the chosen monitors. Ctrl+Z brings it back.");
        ApplyKind(Current.Preset ?? PresetKind.Custom, screens: screens);
    }

    /// <summary>
    /// After the monitors changed or a layout was opened: monitors KDE numbers differently are followed (by device
    /// ID, else place and size); if one is not connected, the layout goes back to 1 monitor with a short notice.
    /// </summary>
    private void CheckScreens()
    {
        if (Current is null || Monitors.Count == 0) return;
        // older layouts remember no screens: a slot on a monitor that is not there counts as missing too
        if (ScreenSet.Resolve(Current, Monitors).Missing || Current.Slots.Any(s => Core.Monitors.Find(Monitors, s.Monitor) is null))
        {
            ApplyKind(Current.Preset ?? PresetKind.Custom, screens: [Core.Monitors.Primary(Monitors)]);
            MonitorNotice = "A monitor of this layout is not connected, so it is on 1 monitor now. Choose the monitors again when it is back (or Ctrl+Z).";
            Status.Warn(MonitorNotice);
            return;
        }
        var remapped = ScreenSet.Remap(Current, Monitors);
        if (ReferenceEquals(remapped, Current)) return;
        Update(remapped, pushUndo: false);
        Status.Info("KDE numbered the monitors differently – the layout stays on its own monitors.");
    }

    /// <summary>6–10 clients on one monitor (Main + side): the parties share it side by side or stacked.</summary>
    public bool HasSplit => Current is { Preset: { } kind } && Presets.HasSplit(kind, Current.Clients, _chosen.Count);

    private PartySplit EffectiveSplit => Current?.Split
        ?? (Current is not null && _chosen.Count > 0 ? Presets.BestSplit(Current.Clients, _chosen[0], Current.Options) : PartySplit.SideBySide);

    public string SplitText => EffectiveSplit == PartySplit.SideBySide ? "Parties side by side" : "Parties stacked, one above the other";

    private void FlipSplit()
    {
        if (!HasSplit) return;
        ApplyKind(Current!.Preset!.Value, split: EffectiveSplit == PartySplit.SideBySide ? PartySplit.Stacked : PartySplit.SideBySide);
        Status.Info($"{SplitText}. Ctrl+Z flips it back.");
    }

    /// <summary>Overlays with up to 5 clients and 2+ monitors: the followers' row can go on monitor 2.</summary>
    public bool CanFollowersOnSecond => Current is { Preset: PresetKind.Overlay, Clients: > 1 and <= Presets.PartySize } && _chosen.Count > 1;

    public bool FollowersOnSecond
    {
        get => Current?.FollowersOnSecond ?? false;
        set
        {
            if (Current is not null && value != Current.FollowersOnSecond) ApplyKind(PresetKind.Overlay, followersOnSecond: value);
        }
    }

    /// <summary>The honest action-bar verdict for the whole layout, and a tip when one monitor holds both parties.</summary>
    public string LayoutAdvice
    {
        get
        {
            if (Current is null || Current.Slots.Count < 2 || Monitors.Count == 0) return "";
            var bars = Enumerable.Range(0, Current.Slots.Count).Select(i => UiScale.EffectiveAspect(PlannedAspect(i), UiScaleNow)).ToList();
            var full = bars.Count(a => a >= 4.0 / 3 - 0.001);
            var text = full == bars.Count
                ? $"The whole action bar fits in all {bars.Count} windows."
                : $"The whole action bar fits in {full} of {bars.Count} windows at UI Scale {UiScaleNow.Display} – Auto-fit or Settings › Fix action bar sets a smaller one.";
            if (Current.Clients > Presets.PartySize && _chosen.Count == 1)
                text += Monitors.Count > 1
                    ? " Two parties on one monitor make small windows: choose 2 monitors to give each party its own."
                    : " Two parties on one monitor make small windows: a second monitor gives each party its own.";
            return text;
        }
    }

    private void LoadProfiles()
    {
        var errors = new List<string>();
        var stored = _store.LoadAll(errors).ToList();   // layouts work with every WoW install
        foreach (var e in errors) Status.Warn($"Skipped a broken layout file: {e}");
        for (var i = 0; i < stored.Count; i++)
            if (stored[i].Profile.Preset == PresetKind.Dual)   // the old "2 monitors" card is Main + side on two monitors now
            {
                var profile = stored[i].Profile with { Preset = PresetKind.Side };
                stored[i] = new StoredProfile(_store.Save(profile, stored[i].Path), profile);
            }
        if (stored.Count == 0 && Monitors.Count > 0)
        {
            // First start: a ready-to-use layout for 3 clients.
            var name = _store.UniqueName("My squad");
            var profile = Presets.Create(PresetKind.Side, 3, Monitors, options: new LayoutOptions { MinAspect = 4.0 / 3 }) with { Name = name };
            stored.Add(new StoredProfile(_store.Save(profile), profile));
        }
        _selectedProfile = null;
        Profiles.Clear();
        foreach (var s in stored) Profiles.Add(new ProfileItem(s));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == LastProfileName) ?? Profiles.FirstOrDefault();
    }

    /// <summary>Selects a layout by name (used by "--start NAME" from a desktop shortcut).</summary>
    public bool SelectProfile(string name)
    {
        var item = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        SelectedProfile = item;
        return true;
    }

    private string? LastProfileName
    {
        get => _settings.LastProfile;
        set
        {
            _settings.LastProfile = value;
            _settings.Save();
        }
    }

    private void AddAndSelect(LayoutProfile profile)
    {
        profile = profile with { Name = _store.UniqueName(profile.Name), Game = null };   // a layout is for any WoW install
        var item = new ProfileItem(new StoredProfile(_store.Save(profile), profile));
        Profiles.Add(item);
        SelectedProfile = item;
    }

    private void SaveAs()
    {
        if (Current is null) return;
        var name = _dialogs.Prompt("Save as a new layout", "Name for the new layout:", Current.Name + " copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        AddAndSelect(Current with { Name = name.Trim() });
        Status.Ok($"Saved \"{SelectedProfile!.Name}\".");
    }

    private void Rename()
    {
        if (_selectedProfile is null || Current is null) return;
        var name = _dialogs.Prompt("Rename layout", "New name:", Current.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == Current.Name) return;
        Update(Current with { Name = _store.UniqueName(name, _selectedProfile.Path) }, pushUndo: false);
    }

    private void Delete()
    {
        if (_selectedProfile is null || Profiles.Count <= 1) return;
        if (!_dialogs.Confirm("Delete layout", $"Delete \"{_selectedProfile.Name}\"? This cannot be undone.")) return;
        _store.Delete(_selectedProfile.Path);
        _drafts.Save(_selectedProfile.Name, null);
        var index = Profiles.IndexOf(_selectedProfile);
        Profiles.Remove(_selectedProfile);
        _selectedProfile = null;
        SelectedProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    /// <summary>Every edit goes through here: undo, auto-save, refresh, live apply.</summary>
    private void Update(LayoutProfile profile, bool pushUndo = true)
    {
        if (_selectedProfile is null || Current is null || profile == Current) return;
        if (pushUndo)
        {
            _undo.Push(new UndoStep(Current));
            _redo.Clear();
        }
        // only a change of the windows' places moves them (a name, class or "on top" change does not)
        var moved = Monitors.Count == 0 || !LayoutEngine.Resolve(Current, Monitors).Select(p => p.Rect).SequenceEqual(LayoutEngine.Resolve(profile, Monitors).Select(p => p.Rect));
        try
        {
            _selectedProfile.Path = _store.Save(profile, _selectedProfile.Path);
        }
        catch (IOException e)
        {
            Status.Warn($"Could not save the layout: {e.Message}");
        }
        _selectedProfile.Profile = profile;
        _selectedProfile.Refresh();
        if (profile.Name != Current.Name) _drafts.Rename(Current.Name, profile.Name);
        Current = profile;
        if (profile.Preset == PresetKind.Custom && _draft is { } draft) SaveDraft(draft with { Custom = profile });   // the draft follows every change
        LastProfileName = profile.Name;
        ApplyOnTop();
        if (moved) ScheduleLive();
        if (_loaded && _settings.AutoFitActionBar && RunningClientsNow() == 0) AutoFitUiScale("the layout changed");
    }

    /// <summary>
    /// A slot edited as the user sees it (canvas, details, squad rows): client <paramref name="index"/> as placed now.
    /// With windows swapped, the place change goes to the slot the client sits in and the name/class stay with the client.
    /// </summary>
    public void OnSlotEdited(int index, LayoutSlot slot)
    {
        if (Current is null || index < 0 || index >= Current.Slots.Count) return;
        var home = WindowSwap.Unapply(Current, _swapPos, index, slot);
        if (CustomLayouts.SameGeometry(Current, home)) Update(home);   // a name, class, on top …
        else UserGeometryEdit(home);                                   // moved or resized: a preset becomes Custom
        RaiseSlotProperties();
    }

    private void EditSlot(Func<LayoutSlot, LayoutSlot> change)
    {
        if (Slot is { } s) OnSlotEdited(SelectedSlotIndex, change(s));
    }

    private void EditSlotRect(PixelRect rect)
    {
        if (Slot is { } s && Current is not null) OnSlotEdited(SelectedSlotIndex, SlotEditing.WithRect(s, rect, ChosenScreens.Count > 0 ? ChosenScreens : Monitors, Current.Options));
    }

    private void EditOptions(Func<LayoutOptions, LayoutOptions> change)
    {
        if (Current is not null) Update(Current with { Options = change(Current.Options) });
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var step = _undo.Pop();
        if (Current is not null) _redo.Push(new UndoStep(Current, step.WindowSlots is null ? null : new Dictionary<nint, int>(_keeper.Slots), step.Accounts is null ? null : _settings.Accounts.ToList()));
        if (step.WindowSlots is { } map)
        {
            // a squad reorder: put the windows and accounts back in their rows too, not only names and classes
            _keeper.Restore(map);
            if (step.Accounts is { } accounts)
            {
                _settings.Accounts = accounts.ToList();
                _settings.Save();
            }
        }
        Update(step.Profile, pushUndo: false);
        if (step.WindowSlots is not null)
        {
            RefreshWindows();
            RefreshParty();
            ApplyOnTop();
            ScheduleLive();
        }
        RaiseSlotProperties();
    }

    private void CopyCode()
    {
        if (Current is null) return;
        _dialogs.CopyToClipboard(ProfileJson.ToShareCode(Current));
        Status.Ok("Layout code copied (WBX1:…). Paste it to a friend or into the website.");
    }

    private void ImportCode()
    {
        var code = _dialogs.AskShareCode(text =>
        {
            var (ok, message, _) = ProfileJson.CheckShareCode(text);
            return (ok, message);
        });
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            AddAndSelect(ProfileJson.FromShareCode(code));
            Status.Ok($"Imported \"{SelectedProfile!.Name}\".");
        }
        catch (FormatException e)
        {
            Status.Warn(e.Message);
        }
    }

    private void ImportFile()
    {
        var path = _dialogs.OpenFile("Layout files (*.json)|*.json");
        if (path is null) return;
        try
        {
            AddAndSelect(ProfileJson.Deserialize(File.ReadAllText(path)));
            Status.Ok($"Imported \"{SelectedProfile!.Name}\" from {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            Status.Warn(e.Message);
        }
    }

    private void ExportFile()
    {
        if (Current is null) return;
        var path = _dialogs.SaveFile("Layout files (*.json)|*.json", ProfileStore.FileNameFor(Current.Name));
        if (path is null) return;
        File.WriteAllText(path, ProfileJson.Serialize(Current));
        Status.Ok($"Saved {Path.GetFileName(path)}.");
    }

    /// <summary>Makes a new layout from where the WoW windows are now (arranged by hand, KWin tiling …).</summary>
    private void Capture()
    {
        var open = Windows.Select(w => w.Window).ToList();
        if (open.Count == 0)
        {
            Status.Warn("No World of Warcraft windows found to capture. Arrange your game windows first.");
            return;
        }
        var borderless = open.All(w => (w.Style & 0x00C00000) == 0);
        var profile = ProfileCapture.FromWindows(open.Select(w => WindowFinder.VisibleRectOf(w.Handle)).ToList(), Monitors, borderless, "Captured layout");
        AddAndSelect(profile);
        Status.Ok($"Captured {open.Count} windows as \"{SelectedProfile!.Name}\".");
    }

    // =====================================================================================
    // Settings
    // =====================================================================================

    public bool RenameWindows
    {
        get => _settings.RenameWindows;
        set => SetSetting(() => _settings.RenameWindows = value);
    }

    public bool Animations
    {
        get => _settings.Animations;
        set => SetSetting(() => _settings.Animations = value);
    }

    /// <summary>Start placing windows as soon as the app opens (off: it opens stopped).</summary>
    public bool StartPlacingOnOpen
    {
        get => _settings.StartPlacingOnOpen;
        set => SetSetting(() => _settings.StartPlacingOnOpen = value);
    }

    /// <summary>Settings › Experimental: one switch per risky feature, all off until the user turns them on.</summary>
    public IReadOnlyList<ExperimentalSwitch> ExperimentalSwitches { get; }

    public bool HasExperimental => ExperimentalSwitches.Count > 0;

    /// <summary>Linux: the Proton the clients start with ("" = automatic: Faugus Launcher's choice for the prefix, else GE-Proton).</summary>
    public string ProtonPath
    {
        get => _settings.ProtonPath ?? "";
        set
        {
            SetSetting(() => _settings.ProtonPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            WinePrefixes.ProtonPathSetting = _settings.ProtonPath;
        }
    }

    public string BattleNetExe
    {
        get => _settings.BattleNetExe ?? BattleNet.FindExe() ?? "";
        set => SetSetting(() => _settings.BattleNetExe = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>The WoW client the app opens and reads (UI Scale, accounts): the open clients' install, else the last used one.</summary>
    public string GameExe
    {
        get => SquadExe ?? _settings.GameExe ?? "";
        set
        {
            SetSetting(() => _settings.GameExe = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            ReadWowAccounts();
            MeasureBars();
        }
    }

    private void BrowseGame()
    {
        var path = _dialogs.OpenFile("WoW client|Wow*.exe|Programs (*.exe)|*.exe");
        if (path is not null) GameExe = path;
    }

    private void SetSetting(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        change();
        _settings.Save();
        OnPropertyChanged(name);
    }

    private void BrowseBattleNet()
    {
        var path = _dialogs.OpenFile("Battle.net|Battle.net.exe|Programs (*.exe)|*.exe");
        if (path is not null) BattleNetExe = path;
    }

    // =====================================================================================
    // Squad
    // =====================================================================================

    public string SquadHint => "Drag ⋮⋮ to change who sits where – the real windows follow. ▾ opens a window's details: class, WoW account, always on top, Identify, size.";

    // ---------- which windows: the squad's install, always watching ----------

    private string? _squadExe;   // a client exe of the install the squad follows (null: no squad window open)
    private readonly HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase);   // other installs the user let in
    private readonly Dictionary<uint, DateTime> _startTimes = [];
    private List<string> _ignoredInstalls = [];
    private bool _firstSync = true;
    private string? _squadInstall;   // the install folder the squad follows (to notice when it changes)

    /// <summary>
    /// The WoW client the squad uses: the install of the open squad windows, else the last used one, else Classic Era's.
    /// UI Scale, accounts and "Open WoW clients" follow it.
    /// </summary>
    public string? SquadExe => _squadExe ?? (File.Exists(_settings.GameExe) ? _settings.GameExe : GameInstall.FindExe(null));

    public WowGame SquadGame => GameDetection.GameOf(SquadExe);

    public string SquadGameName => GameDetection.DisplayName(SquadGame);

    /// <summary>Open WoW windows of other installs, left alone ("1 Forever window ignored"); "" when none.</summary>
    public string IgnoredText { get; private set; } = "";

    /// <summary>More squad windows than slots: the extra ones stay where they are; "" when none.</summary>
    public string ExtraText { get; private set; } = "";

    private static string InstallOf(WindowInfo w) => Path.GetDirectoryName(w.ExePath) ?? "";

    private DateTime StartedAt(uint pid)
    {
        if (!_startTimes.TryGetValue(pid, out var t)) _startTimes[pid] = t = ProcessInfo.StartTime(pid) ?? DateTime.MaxValue;
        return t;
    }

    private void Include()
    {
        foreach (var install in _ignoredInstalls) _included.Add(install);
        Status.Info("The other WoW windows join the squad now (until the app restarts).");
        RefreshWindows();
    }

    /// <summary>
    /// Looks at every open WoW window: the squad follows the install with the most windows (tie: the one opened first);
    /// each squad window keeps its slot, a new one takes the slot it already sits in or the next free one. Windows of
    /// other installs are left alone. Placing happens in <see cref="Tick"/> (not while paused).
    /// </summary>
    public void RefreshWindows()
    {
        if (Current is null) return;
        var all = WindowFinder.FindGameWindows();
        var install = GameDetection.SquadInstall(all.Select(w => (InstallOf(w), StartedAt(w.Pid))), _squadInstall);
        bool InSquad(WindowInfo w) => string.Equals(InstallOf(w), install, StringComparison.OrdinalIgnoreCase) || _included.Contains(InstallOf(w));
        var squad = all.Where(InSquad).OrderBy(w => StartedAt(w.Pid)).ToList();
        var ignored = all.Where(w => !InSquad(w)).ToList();

        var exe = squad.FirstOrDefault(w => string.Equals(InstallOf(w), install, StringComparison.OrdinalIgnoreCase))?.ExePath;
        if (!string.Equals(exe ?? _squadExe, _squadExe, StringComparison.OrdinalIgnoreCase))
        {
            _squadExe = exe;
            _settings.GameExe = exe;   // the last used install: "Open WoW clients" uses it when nothing is open
            _settings.Save();
            foreach (var name in new[] { nameof(GameExe), nameof(SquadGame), nameof(SquadGameName) }) OnPropertyChanged(name);
            if (!_firstSync) ReadWowAccounts();
        }
        _squadExe = exe ?? _squadExe;

        _ignoredInstalls = ignored.Select(InstallOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IgnoredText = string.Join(" · ", ignored.GroupBy(w => w.Game).Select(g => $"{g.Count()} {GameDetection.DisplayName(g.Key)} window{(g.Count() == 1 ? "" : "s")} ignored"));

        var switched = install is not null && _squadInstall is not null && !string.Equals(install, _squadInstall, StringComparison.OrdinalIgnoreCase);
        _squadInstall = install ?? _squadInstall;
        var (added, removed) = _keeper.Sync(squad, Current.Slots.Count, SitsIn);
        if (_firstSync)
        {
            if (added.Count > 0) Status.Info($"Found {added.Count} open WoW window(s): each keeps its slot or gets the next free one.");
        }
        else if (switched)
        {
            Status.Info($"The squad now follows the {GameDetection.DisplayName(GameDetection.GameOf(exe))} install with the most open windows ({squad.Count}); other WoW windows are left alone.");
        }
        else
        {
            foreach (var (h, slot) in removed)
                Status.Info(WindowFinder.Exists(h) ? $"Window {slot + 1}'s slot is gone (fewer clients): that window is left where it is." : $"Window {slot + 1} closed – its slot is free again.");
            foreach (var (_, slot) in added)
                Status.Ok($"New WoW window → window {slot + 1} ({Party.ElementAtOrDefault(slot)?.Title ?? $"slot {slot + 1}"}).");
        }
        if (_firstSync) AfterFirstSwapSync();
        _firstSync = false;
        var extra = squad.Where(w => !_keeper.Slots.ContainsKey(w.Handle)).ToList();
        ExtraText = extra.Count == 0 ? ""
            : string.Join(" · ", extra.GroupBy(w => w.Game).Select(g => $"{g.Count()} extra {GameDetection.DisplayName(g.Key)} window{(g.Count() == 1 ? "" : "s")}"))
              + $" left as {(extra.Count == 1 ? "it is" : "they are")} – more windows than slots";
        OnPropertyChanged(nameof(IgnoredText));
        OnPropertyChanged(nameof(ExtraText));

        var byHandle = squad.ToDictionary(w => w.Handle);
        var mapping = _keeper.Slots.Where(kv => byHandle.ContainsKey(kv.Key)).OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value + 1)).ToList();
        if (!mapping.SequenceEqual(Windows.Select(w => (w.Window.Handle, w.Slot))))
        {
            Windows.Clear();
            foreach (var (h, slot) in mapping) Windows.Add(new GameWindowItem(byHandle[h]) { Slot = slot });
            CommandManager.InvalidateRequerySuggested();
        }
        if (added.Count > 0) ApplyOnTop();
        UpdatePartyWindows();
    }

    /// <summary>The slot a window already sits in exactly (so windows arranged earlier keep their places), or -1.</summary>
    private int SitsIn(nint h)
    {
        if (Placed is not { } layout || Monitors.Count == 0) return -1;
        var placed = LayoutEngine.Resolve(layout, Monitors);
        return Enumerable.Range(0, placed.Count).FirstOrDefault(i => WindowPlacer.IsAt(h, placed[i].Rect), -1);
    }

    private GameWindowItem? WindowAt(int index) => Windows.FirstOrDefault(w => w.Slot == index + 1);

    /// <summary>
    /// Called every second while the app runs (also in the tray): new windows get their slot and are placed, "on top"
    /// stays right, and when the last client of the install closes, Auto-fit may set the UI Scale.
    /// </summary>
    public void Tick()
    {
        Hover.Refresh();   // keeps the switch in step with what the agent has
        if (!Busy) RefreshWindows();
        if (!_paused)
        {
            _guard?.Reassert();   // safety net next to the event hooks
            if (!_stepping && !Busy && Plan() is { } plan) _ = StepAsync(plan);
        }
        var running = RunningClientsNow();
        if (running == 0 && _lastRunning > 0) AutoFitUiScale("the last client closed");
        _lastRunning = running;
        if (_openingUntil != default && !Opening)
        {
            _openingUntil = default;
            RaiseSquadStatus();
        }
    }

    private async Task StepAsync(SquadPlan plan)
    {
        _stepping = true;
        try
        {
            await _keeper.StepAsync(plan, Status);
        }
        finally
        {
            _stepping = false;
        }
    }

    // ---------- status and the one button ----------

    /// <summary>Slots whose window is open and in place.</summary>
    public int ConnectedCount => Party.Count(p => p.State == PartyState.Connected);

    /// <summary>Slots without a window yet.</summary>
    public int Missing => Current is null ? 0 : Math.Max(0, Current.Slots.Count - Windows.Count);

    /// <summary>Just after "Open N WoW clients", until the windows are there (or 2 minutes passed).</summary>
    private bool Opening => DateTime.UtcNow < _openingUntil && Missing > 0;

    private int SlotCount => Current?.Slots.Count ?? 0;

    /// <summary>Title bar: "Awaiting clients · 1 of 3 connected" → "All 3 connected".</summary>
    public string HeaderStatus => _paused ? "Stopped – WoW windows are left alone"
        : Missing > 0 ? $"Awaiting clients · {ConnectedCount} of {SlotCount} connected"
        : ConnectedCount == SlotCount ? (SlotCount == 1 ? "Connected" : $"All {SlotCount} connected")
        : $"{ConnectedCount} of {SlotCount} in place · Re-arrange puts them back";

    /// <summary>For the title bar's dot: Paused, Ready (all connected), Awaiting, Moved.</summary>
    public string SquadState => _paused ? "Paused" : Missing > 0 ? "Awaiting" : ConnectedCount == SlotCount ? "Ready" : "Moved";

    /// <summary>Started: windows are placed (the Stop button shows).</summary>
    public bool Placing => !_paused;

    public string PrimaryText => _paused ? "START PLACING WINDOWS"
        : Opening ? $"AWAITING CLIENTS {ConnectedCount}/{SlotCount}"
        : Missing > 0 ? $"OPEN {Missing} WOW CLIENT{(Missing == 1 ? "" : "S")}"
        : "RE-ARRANGE";

    public string PrimaryTip => _paused ? "Starts placing: each WoW window of the squad goes to its slot, as on the map. Until then the app leaves every window alone."
        : Opening ? "Log in to each login screen yourself – each window snaps into its slot as it opens."
        : Missing > 0 ? $"Opens {Missing} {SquadGameName} login screen(s), one per empty slot. You log in to each yourself – nothing is typed for you. (Ctrl+Enter)"
        : "Snaps every WoW window back into its slot (Ctrl+Enter or F5).";

    private void RaiseSquadStatus()
    {
        foreach (var name in new[] { nameof(HeaderStatus), nameof(SquadState), nameof(Placing), nameof(PrimaryText), nameof(PrimaryTip), nameof(ConnectedCount), nameof(Missing), nameof(SlotStates) })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Per slot: Awaiting / Placing / Connected / Moved (the map draws it on each window).</summary>
    public IReadOnlyList<PartyState> SlotStates => Party.Select(p => p.State).ToList();

    private void Primary()
    {
        if (_paused) Paused = false;
        else if (Missing > 0) OpenClients();
        else _ = ArrangeAsync();
    }

    /// <summary>
    /// "Open N WoW clients": one login screen per empty slot, from the squad's install (the open clients' one, else the
    /// last used). The user logs in to each – nothing is typed or clicked for them.
    /// </summary>
    private void OpenClients()
    {
        if (Current is null || Missing == 0) return;
        if (SquadExe is not { } exe)
        {
            Status.Warn("WoW was not found. Set the WoW client in Settings.");
            return;
        }
        if (RunningClientsNow() == 0) AutoFitUiScale("before opening the clients");
        Paused = false;   // opening clients means: place them
        var missing = Missing;
        Status.Title($"Opening {missing} {SquadGameName} login screen(s) for \"{Current.Name}\"");
        if (_launcher.Start(exe, missing, Status) == 0) return;
        _openingUntil = DateTime.UtcNow.AddMinutes(2);
        for (var i = 0; i < Party.Count; i++)
        {
            if (WindowAt(i) is not null) continue;
            var account = i < _settings.Accounts.Count && _settings.Accounts[i].Length > 0 ? $"  ({_settings.Accounts[i]})" : "";
            Status.Info($"   Window {i + 1}: {Party[i].Title}{account}");
        }
        RaiseSquadStatus();
    }

    /// <summary>"Open Battle.net instead": starts it if needed and says what to press. Never clicks anything in it.</summary>
    private void OpenBattleNet()
    {
        SquadWatcher.EnsureBattleNet(Status, _settings.BattleNetExe, ProcessInfo.CurrentElevation() == Elevation.Elevated);
        Status.Info(SquadWatcher.BattleNetInstructions(SquadGame == WowGame.Forever));
        Status.Info("Then press Play once per account – each window snaps into its slot as it opens.");
    }

    // ---------- rows ----------

    /// <summary>
    /// Rows mirror the layout's slots (name, class) plus the local WoW-account choice. Rows are reused when the slot
    /// count is unchanged, so editing one field never rebuilds the list under the cursor.
    /// </summary>
    private void RefreshParty()
    {
        if (Current is null) return;
        if (Party.Count != Current.Slots.Count)
        {
            var accounts = WowAccounts.Select(a => a.Name).Prepend("").ToList();
            var lastPlayed = WowAccounts.ToDictionary(a => a.Name, a => a.LastPlayed, StringComparer.OrdinalIgnoreCase);
            Party.Clear();
            var parties = Current.Slots.Count > Presets.PartySize;
            var followers = new int[3];
            for (var i = 0; i < Current.Slots.Count; i++)
            {
                var index = i;
                var party = Presets.PartyOf(i + 1);
                var main = Current.Slots[i].Role == SlotRole.Main;
                Party.Add(new PartyMember
                {
                    Number = i + 1,
                    Role = main ? "Main" : "Follower",
                    FollowerNumber = main ? 0 : ++followers[party],
                    PartyName = parties ? $"Party {party}" : "",
                    PartyHeader = parties && (i == 0 || Presets.PartyOf(i) != party) ? $"Party {party}" : "",
                    Accounts = accounts,
                    LastPlayed = lastPlayed,
                    Identify = new RelayCommand(() => IdentifySlot(index)),
                    ToggleDetails = new RelayCommand(() => ToggleDetails(index)),
                    NameChanged = (k, name) =>
                    {
                        MarkSlotEdited(k);
                        EditSlotAt(k, s => s with { Name = name.Length == 0 ? null : name });
                    },
                    ClassChanged = (k, c) =>
                    {
                        MarkSlotEdited(k);
                        EditSlotAt(k, s => s with { Class = c == WowClasses.Automatic ? null : c.Id });
                    },
                    AccountChanged = SaveAccount,
                    CombatRoleChanged = (k, r) => EditSlotAt(k, s => s with { CombatRole = Enum.TryParse<CombatRole>(r, out var cr) ? cr : null }),
                    IsExpanded = index == ExpandedIndex,
                });
            }
            if (ExpandedIndex >= Party.Count) ExpandedIndex = -1;
        }
        for (var i = 0; i < Party.Count; i++)
        {
            var slot = Current.Slots[i];
            Party[i].Load(slot.Name ?? "", WowClasses.Find(slot.Class) ?? WowClasses.Automatic, i < _settings.Accounts.Count ? _settings.Accounts[i] : "");
            Party[i].LoadCombatRole(slot.CombatRole?.ToString() ?? "");
        }
        MarkAddOnRows();
        UpdatePartyWindows();
    }

    /// <summary>WoW accounts and characters from the squad install's WTF folder (read-only hints).</summary>
    public IReadOnlyList<WtfAccount> WowAccounts { get; private set; } = [];

    private void ReadWowAccounts()
    {
        try
        {
            WowAccounts = GameInstall.Accounts(SquadExe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WowAccounts = [];
        }
        Party.Clear();   // rebuild rows with the new lists
        RefreshParty();
        ApplyAddOn();   // the squad's install (and so its WoWBoxer.lua files) may have changed
    }

    private void EditSlotAt(int index, Func<LayoutSlot, LayoutSlot> change)
    {
        if (Current is null || index < 0 || index >= Current.Slots.Count) return;
        OnSlotEdited(index, change(Placed!.Slots[index]));
    }

    /// <summary>
    /// Squad drag and drop: the character in row <paramref name="from"/> (name, class, WoW account and its open window)
    /// moves to slot position <paramref name="to"/>; the others shift. Slot rectangles stay where they are, so the
    /// canvas, the rows and the real windows all follow the same order.
    /// </summary>
    public void MoveCharacter(int from, int to)
    {
        if (Current is null || from == to || from < 0 || to < 0 || from >= Current.Slots.Count || to >= Current.Slots.Count) return;
        ResetSwap(quiet: true);   // rows are reordered at home, never while swapped
        var before = new UndoStep(Current, new Dictionary<nint, int>(_keeper.Slots), _settings.Accounts.ToList());
        var people = Current.Slots.Select(s => (s.Name, s.Class, s.CombatRole)).ToList();
        var person = people[from];
        people.RemoveAt(from);
        people.Insert(to, person);
        var slots = Current.Slots.Select((s, i) => s with { Name = people[i].Name, Class = people[i].Class, CombatRole = people[i].CombatRole }).ToList();

        while (_settings.Accounts.Count < Current.Slots.Count) _settings.Accounts.Add("");
        var account = _settings.Accounts[from];
        _settings.Accounts.RemoveAt(from);
        _settings.Accounts.Insert(to, account);
        MoveSlotEdited(from, to, Current.Slots.Count);
        _settings.Save();

        _keeper.Move(from, to, Current.Slots.Count);
        _undo.Push(before);   // also when the names did not change (unnamed rows): the windows did
        _redo.Clear();
        Update(Current with { Slots = slots }, pushUndo: false);
        // The window order changed even when names did not (unnamed rows): always refresh and move the windows.
        RefreshWindows();
        RefreshParty();
        ApplyOnTop();
        ScheduleLive();
        Status.Info($"Moved {person.Name ?? $"row {from + 1}"} to slot {to + 1}.");
    }

    /// <summary>
    /// Canvas drag and drop: dropping window <paramref name="a"/> onto window <paramref name="b"/> swaps their places on
    /// screen. Each keeps its number, character and class; the real windows swap too.
    /// </summary>
    public void SwapSlots(int a, int b)
    {
        if (Current is null || a == b || a < 0 || b < 0 || a >= Current.Slots.Count || b >= Current.Slots.Count) return;
        ResetSwap(quiet: true);   // the canvas swap edits the home layout
        LayoutSlot sa = Current.Slots[a], sb = Current.Slots[b];
        var slots = Current.Slots.ToList();
        slots[a] = sa with { Monitor = sb.Monitor, X = sb.X, Y = sb.Y, W = sb.W, H = sb.H, Z = sb.Z, Anchor = sb.Anchor, Fit = sb.Fit };
        slots[b] = sb with { Monitor = sa.Monitor, X = sa.X, Y = sa.Y, W = sa.W, H = sa.H, Z = sa.Z, Anchor = sa.Anchor, Fit = sa.Fit };
        UserGeometryEdit(Current with { Slots = slots });
        SelectedSlotIndex = a;
        Status.Info($"Swapped the places of window {a + 1} and window {b + 1}. Ctrl+Z undoes it.");
    }

    /// <summary>Row statuses from the open windows: Awaiting → Placing → Connected (or Moved when it left its slot).</summary>
    private void UpdatePartyWindows()
    {
        if (Placed is not { } layout || Monitors.Count == 0) return;
        var placed = LayoutEngine.Resolve(layout, Monitors);
        UpdateSwapBadges();
        for (var i = 0; i < Party.Count; i++)
        {
            var w = WindowAt(i);
            Party[i].Window = w is null ? "No window yet" : $"{w.Process} · {w.Detail}";
            Party[i].State = w is null ? PartyState.Awaiting
                : i < placed.Count && WindowPlacer.IsAt(w.Window.Handle, placed[i].Rect) ? PartyState.Connected
                : !_paused && _keeper.IsSettling(w.Window.Handle) ? PartyState.Placing
                : PartyState.Moved;
        }
        RaiseSquadStatus();
        MeasureBars();
    }

    private void IdentifySlot(int index)
    {
        RefreshWindows();
        if (WindowAt(index) is not { } w)
        {
            Status.Warn($"Slot {index + 1} has no WoW window yet.");
            return;
        }
        _dialogs.ShowIdentify([(WindowFinder.VisibleRectOf(w.Window.Handle), w.Slot.ToString(), Party.ElementAtOrDefault(index)?.Title ?? w.Process)]);
    }

    public bool AutoFitActionBar
    {
        get => _settings.AutoFitActionBar;
        set
        {
            SetSetting(() => _settings.AutoFitActionBar = value);
            if (value && RunningClientsNow() == 0) AutoFitUiScale("Auto-fit was switched on");
            RaiseSlotProperties();
        }
    }

    private string _uiScaleNotice = "";

    /// <summary>A short line on the map after Auto-fit changed the UI Scale ("" = nothing to say).</summary>
    public string UiScaleNotice { get => _uiScaleNotice; private set => Set(ref _uiScaleNotice, value); }

    /// <summary>
    /// "Auto-fit action bar": while no client of the install runs (WoW rewrites Config.wtf when a client closes), set the
    /// one UI Scale at which every window of the current layout shows the whole bar – only if the current one is too big.
    /// </summary>
    private void AutoFitUiScale(string why)
    {
        // Only where the bar rule is verified in game (Classic Era); other games' UI is not known yet – the button still works there.
        if (!_settings.AutoFitActionBar || Current is null || SquadGame != WowGame.ClassicEra) return;
        var need = UiScale.ForSquad(Enumerable.Range(0, Current.Slots.Count).Select(PlannedAspect));
        if (need is null || SquadExe is not { } exe) return;
        var now = UiScaleFix.Read(exe);
        if (now.Use && now.Scale <= need.Value + 0.001) return;
        if (UiScaleFix.Write(exe, use: true, need.Value) is { } error)
        {
            Status.Warn("Auto-fit action bar: " + error);
            return;
        }
        MeasureBars();
        UiScaleNotice = $"UI Scale set to {need:0.00} so the full action bar fits in every window ({why}; backup kept).";
        Status.Ok($"Auto-fit action bar: UI Scale set to {need:0.00} in {SquadGameName}'s Config.wtf so the full bar fits in every window of \"{Current.Name}\" ({why}; backup kept).");
    }

    private void SaveAccount(int index, string account)
    {
        while (_settings.Accounts.Count <= index) _settings.Accounts.Add("");
        _settings.Accounts[index] = account.Trim();
        _settings.Save();
        UpdatePartyWindows();
        ApplyAddOn();   // a newly chosen account brings its AddOn character
    }

    private void Identify()
    {
        RefreshWindows();
        if (Windows.Count == 0)
        {
            Status.Warn("No World of Warcraft windows found.");
            return;
        }
        var labels = Windows.Select(w =>
        {
            var name = Current is not null && w.Slot - 1 < Current.Slots.Count ? Current.Slots[w.Slot - 1].Name : null;
            return (WindowFinder.VisibleRectOf(w.Window.Handle), w.Slot.ToString(), name ?? w.Process);
        }).ToList();
        _dialogs.ShowIdentify(labels);
        Status.Info("Showing the slot number on each WoW window. Drag the rows to change who goes where.");
    }

    private SquadPlan? Plan()
    {
        if (Placed is not { } placed) return null;
        RefreshMonitors();
        return new SquadPlan(placed, Monitors, RenameWindows && placed.Slots.Any(s => s.Name is { Length: > 0 }));
    }

    /// <summary>The open squad windows with their slot index (slots without a window are left out).</summary>
    private List<(WindowInfo Window, int Slot)> Placements() =>
        Windows.Where(w => WindowFinder.Exists(w.Window.Handle)).Select(w => (w.Window, w.Slot - 1)).ToList();

    /// <summary>"Re-arrange": snaps every open squad window back into its slot.</summary>
    private async Task ArrangeAsync()
    {
        ResetSwap(quiet: true);   // Re-arrange = every client back in its home slot
        if (Plan() is not { } plan) return;
        RefreshWindows();
        var placements = Placements();
        Busy = true;
        try
        {
            Status.Title($"Re-arrange: \"{plan.Profile.Name}\"");
            await Task.Run(() => Arranger.ArrangeAsync(plan, placements, Status));
        }
        finally
        {
            Busy = false;
            RefreshWindows();
        }
    }

    /// <summary>
    /// Makes "always on top" of the open windows match their slots right away (set and cleared, no move). Runs at start,
    /// after every edit and after reordering.
    /// </summary>
    private void ApplyOnTop()
    {
        if (Current is null) return;
        if (_guard is not null)
        {
            _guard.Reassert(ours: true);   // through the guard, so it does not mistake our own change for someone else's
            return;
        }
        foreach (var (h, z) in WantedZOrder()) WindowPlacer.ApplyZOrder(h, z);
    }

    /// <summary>After an edit, move the open WoW windows to the new layout (debounced, quiet). Not while paused.</summary>
    private void ScheduleLive()
    {
        if (!_loaded || _paused || Busy) return;
        _live?.Cancel();
        _live = new CancellationTokenSource();
        _ = LiveAsync(_live.Token);
    }

    private async Task LiveAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(350, cancel);
            if (Windows.Count == 0 || Plan() is not { } plan) return;
            var placements = Placements();
            await _liveLock.WaitAsync(cancel);
            try
            {
                var quiet = new QuietSink(Status);
                var ok = await Task.Run(() => Arranger.ArrangeAsync(plan, placements, quiet, cancel), cancel);
                if (ok) Status.Ok($"Moved {placements.Count} WoW window(s) to \"{plan.Profile.Name}\".");
                RefreshWindows();
            }
            finally
            {
                _liveLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // a newer edit replaced this one
        }
        catch (Exception e)
        {
            Status.Report(StatusLevel.Error, $"Moving the windows failed: {e.Message}");
        }
    }

    /// <summary>Only warnings and errors get through (live moves would otherwise flood the activity list).</summary>
    private sealed class QuietSink(IStatusSink inner) : IStatusSink
    {
        public void Report(StatusLevel level, string message)
        {
            if (level is StatusLevel.Warn or StatusLevel.Error or StatusLevel.Detail) inner.Report(level, message);
        }
    }

    /// <summary>Desktop shortcut ("--start NAME"): that layout, and the missing clients' login screens.</summary>
    public void StartLayout(string name)
    {
        if (!SelectProfile(name)) return;
        Paused = false;   // a layout shortcut means: start
        RefreshWindows();
        if (Missing > 0) OpenClients();
        else _ = ArrangeAsync();
    }

    private void RestoreFrames()
    {
        var open = Windows.Select(w => w.Window).ToList();
        foreach (var w in open) WindowPlacer.RestoreFrame(w.Handle);
        Status.Info($"Gave {open.Count} WoW window(s) their title bars back.");
    }

    /// <summary>
    /// Applies or restores window frames on mapped WoW windows right away (even while paused).
    /// Later Start / Re-arrange keep using Profile.Options.Borderless.
    /// </summary>
    private void ApplyBorderlessChrome()
    {
        if (Current is null || Plan() is not { } plan) return;
        RefreshWindows();
        var placements = Placements();
        if (placements.Count == 0) return;
        foreach (var (window, slot) in placements)
            plan.Place(window.Handle, slot);
        Status.Info(Current.Options.Borderless
            ? $"Removed title bars on {placements.Count} WoW window(s)."
            : $"Restored title bars on {placements.Count} WoW window(s).");
    }

    /// <summary>Writes the Diagnose report (the same as "wbx diagnose") to the logs folder.</summary>
    private async Task DiagnoseAsync()
    {
        if (Plan() is not { } plan) return;
        RefreshWindows();
        var path = Path.Combine(StatusLog.LogFolder, $"diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var report = new ReportSink();
        Busy = true;
        try
        {
            Status.Title("Diagnose: writing a report and trying one placement per window …");
            var windows = Windows.Select(w => w.Window).ToList();
            await Task.Run(() => Diagnostics.RunAsync(plan, windows, report, $"WoWBoxer {Version}"));
            Directory.CreateDirectory(StatusLog.LogFolder);
            File.WriteAllText(path, report.Text.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~", StringComparison.Ordinal));
            Status.Ok($"Diagnose report saved: {path}");
            Open(StatusLog.LogFolder);
        }
        finally
        {
            Busy = false;
            RefreshWindows();
        }
    }

    private sealed class ReportSink : IStatusSink
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text) return _text.ToString();
            }
        }

        public void Report(StatusLevel level, string message)
        {
            lock (_text) _text.AppendLine((level == StatusLevel.Title ? Environment.NewLine : "") + message);
        }
    }

    // =====================================================================================
    // Report a problem (Discord only; the user sees and pastes everything – nothing is sent)
    // =====================================================================================

    /// <summary>What must not be in a report: the home folder and user name, WoW account and character names.</summary>
    public ProblemReport.Secrets ReportSecrets() => new(
        [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
        [Environment.UserName],
        [.. WowAccounts.Select(a => a.Name), .. _settings.Accounts.Where(a => a.Length > 0)],
        [.. WowAccounts.SelectMany(a => a.Characters.Select(c => c.Name)), .. Current?.Slots.Select(s => s.Name ?? "") ?? [], .. Party.Select(p => p.Name)]);

    /// <summary>The short report: version, system, layout, monitors, windows, action bar, hover, the last 50 log lines – redacted.</summary>
    public string BuildReport()
    {
        RefreshWindows();
        var hover = HoverActivation.Read();
        var items = new List<(string, string)>
        {
            ("App", $"WoWBoxer {Version} · {(_paused ? "stopped" : "placing windows")}"),
            ("System", $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"} session · {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"}"),
            ("Layout", Current is null ? "none"
                : $"{(Current.Preset is { } p ? Presets.DisplayName(p) : "custom")} · {Current.Clients} clients · {ChosenScreens.Count} of {Monitors.Count} monitor(s)"
                  + $"{(HasSplit ? " · " + SplitText.ToLowerInvariant() : "")}{(Current.Options.Borderless ? " · borderless" : "")}{(Current.Options.UseTaskbarArea ? " · covers taskbar" : "")}"),
            ("Monitors", string.Join(" · ", Monitors.Select(m =>
                $"{m.Bounds.Width}×{m.Bounds.Height} {m.ScalePercent} %{(m.IsPrimary ? " primary" : "")}{(ChosenScreens.Any(c => c.Index == m.Index) ? " (used)" : "")}"))),
            ("Game", $"{SquadGameName} · {Windows.Count} window(s){(IgnoredText.Length > 0 ? " · " + IgnoredText : "")}{(ExtraText.Length > 0 ? " · " + ExtraText : "")}"),
            ("Slots", string.Join(" · ", Party.Select((m, i) => $"{m.Number} {m.StateText.TrimEnd('…', '.')}{(LiveSlots.TryGetValue(i, out var l) ? $" {l.Width}×{l.Height}{(l.Topmost ? " top" : "")}" : "")}"))),
            ("Action bar", $"UI Scale {UiScaleNow.Display} · auto-fit {(_settings.AutoFitActionBar ? "on" : "off")} · {LayoutAdvice}"),
            ("Hover", hover.Describe()),
        };
        var report = ProblemReport.Lines(items) + "Last log lines:\n" + string.Join("\n", LastLogLines(50));
        return ProblemReport.Redact(report, ReportSecrets());
    }

    private static IEnumerable<string> LastLogLines(int count)
    {
        try
        {
            using var stream = new FileStream(StatusLog.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                lines.Enqueue(line);
                if (lines.Count > count) lines.Dequeue();
            }
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ["(no log yet)"];
        }
    }

    /// <summary>
    /// The full report for "Post in Discord": the (edited) short report, a read-only Diagnose (no window is moved) and
    /// the last 300 log lines, redacted, saved in the logs folder. Returns the file's path.
    /// </summary>
    public string SaveFullReport(string shortReport)
    {
        var diagnose = new ReportSink();
        if (Plan() is { } plan)
        {
            var windows = Windows.Select(w => w.Window).ToList();
            // off the UI thread: the Linux diagnose asks KWin over D-Bus, whose answers must not wait for this thread
            Task.Run(() => Diagnostics.RunAsync(plan, windows, diagnose, $"WoWBoxer {Version}", tryPlacement: false)).GetAwaiter().GetResult();
        }
        var text = shortReport.TrimEnd() + "\n\n===== Diagnose (read-only) =====\n" + diagnose.Text + "\n===== Log (last 300 lines) =====\n" + string.Join("\n", LastLogLines(300));
        var path = Path.Combine(StatusLog.LogFolder, $"wowboxer-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(StatusLog.LogFolder);
        File.WriteAllText(path, ProblemReport.Redact(text, ReportSecrets()));
        Status.Info($"Problem report saved: {Path.GetFileName(path)} (in the logs folder).");
        return path;
    }

    /// <summary>The Guide chapter about switching windows (from the hover notice).</summary>
    /// <summary>Settings › Advanced's link: the Guide chapter "Swap windows (advanced)".</summary>
    public ICommand OpenSwapGuideCommand => new RelayCommand(() =>
    {
        Guide.Current = Guide.Chapters.First(c => c.Number == GuideViewModel.SwapChapter);
        SelectedTab = AppTab.Guide;
    });

    public string SwapNote => GuideViewModel.SwapNote;

    private void OpenSwitchingGuide()
    {
        Guide.Current = Guide.Chapters.First(c => c.Number == GuideViewModel.SwitchingChapter);
        SelectedTab = AppTab.Guide;
    }

    private static void Open(string folder) => Shell.OpenFolder(folder);

    private static void OpenUrl(string? url) => Shell.OpenUrl(url);
}
