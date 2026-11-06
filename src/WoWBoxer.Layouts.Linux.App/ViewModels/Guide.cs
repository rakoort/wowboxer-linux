using System.Windows.Input;
using WoWBoxer.Layouts.App.Infrastructure;

namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>One chapter of the quick guide (quest-log style: objectives, a "good to know" note, an optional helper).</summary>
public sealed class GuideChapter(int number, string title, string intro, string[] objectives, string note, string? action = null, ICommand? actionCommand = null,
    HoverViewModel? hover = null) : ObservableObject
{
    private bool _isCurrent;

    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Intro { get; } = intro;
    public IReadOnlyList<string> Objectives { get; } = objectives;
    public string Note { get; } = note;
    public string? Action { get; } = action;
    public ICommand? ActionCommand { get; } = actionCommand;
    public bool HasAction => Action is not null;
    /// <summary>The chapter's own hover-to-activate switch (Switch between windows only).</summary>
    public HoverViewModel? Hover { get; } = hover;
    public bool HasHover => Hover is not null;
    public string Mark => IsCurrent ? "!" : "?";

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (Set(ref _isCurrent, value)) OnPropertyChanged(nameof(Mark));
        }
    }
}

public sealed class GuideViewModel : ObservableObject
{
    private GuideChapter _current;

    public const int CustomChapter = 3;
    public const int SwitchingChapter = 5;
    public const int SwapChapter = 6;

    /// <summary>The note shown with window swap (Guide and Settings › Advanced). Facts only; the owner's wording (2026-11-02).</summary>
    public const string SwapNote = "Moves windows only – nothing is sent to the game. Blizzard has not ruled on window-swap tools; use at your own judgement.";

    /// <param name="openHelp">Opens the Help tab (troubleshooting, Report a problem).</param>
    /// <param name="openAdvanced">Opens Settings (window swap lives in Settings › Advanced).</param>
    public GuideViewModel(HoverViewModel hover, ICommand openHelp, ICommand openAdvanced)
    {
        Chapters =
        [
            new(1, "Prepare your WoW clients",
                "A few game settings make every window look sharp and run smoothly. You only do this once – all clients from one install share them.",
                [
                    "Esc → Options → Graphics → Display Mode: Windowed. Forever starts in \"Windowed (Fullscreen)\" – then it maximises itself again and cannot be placed, so switch it once.",
                    "Render Scale: 100 % so the picture stays crisp in small windows.",
                    "Raise Background FPS (Graphics → Advanced, or /console maxFPSBk 30) so follower windows stay smooth while you play the main.",
                ],
                "Keep the title bars on (the app's default). In our tests WoW sometimes drew its action bars at the wrong size after a title bar was removed."),
            new(2, "Pick clients, a layout and your monitors",
                "On the Setup tab, choose how many clients you run, pick a layout and, with two or more monitors, how many of them to use. Your screens update instantly.",
                [
                    "Choose 2–10 clients with the number buttons – or 1 for a normal single window when you play one character (no need to close the app). Clients 1–5 are party 1, 6–10 party 2, with client 6 as party 2's main.",
                    "Click a layout card: Main + side, Overlays, Grid or Custom. Each card shows a live preview for your clients and monitors.",
                    "Monitors (right below, with 2+ monitors): 1 monitor, 2 monitors … The map shows exactly the chosen ones, numbered 1, 2 …; the main goes on 1. With fewer than all, \"Which ones\" picks them.",
                    "Main + side on 2+ monitors: the main fills monitor 1, the followers go on the others. With 6–10 clients each party gets its own block – one per monitor, or two halves of one monitor (Flip turns the halves). Custom: drag windows anywhere, also across monitors.",
                    "Fine-tune by dragging windows on the map. A green dot means the whole action bar fits; a solid dot is measured on the open game window, a ring is the plan.",
                ],
                "Everything is saved automatically – clients, layout and monitors – and the app opens with your last setup next time. If a monitor of the layout is unplugged, it goes back to 1 monitor and tells you. Layouts work with every WoW install."),
            new(CustomChapter, "Build your own layout (Custom)",
                "Custom is your sandbox: start from any layout and change it until it is exactly right. Nothing is ever lost – every change is kept as a draft, also when you close the app.",
                [
                    "Drag or resize any window on the map: the layout becomes Custom from exactly that arrangement (nothing jumps). Or click the Custom card – it keeps what is on the map, or brings your last custom draft back.",
                    "ADVANCED EDITING (the button on the map's header) opens a toolbar. Click a window to select it, Ctrl or Shift+click for more, or drag a box around them on the empty map; Ctrl+A selects all, Esc clears. The first one you pick is the reference (gold outline).",
                    "Drag any selected window and the whole group moves together; arrow keys nudge it 1 px, Shift+arrow 10 px. Same width / height / size (Ctrl+Shift+M) copy the reference's size; Align lines them up with it – or with the monitor when one window is selected.",
                    "Distribute spaces 3 or more windows evenly. Fill row / Fill column makes the selected windows the same size and fills the monitor's width or height edge to edge in their order (optional gap). Fit 4:3 makes the whole Classic action bar fit. Type exact X, Y, W, H for the selection under the toolbar.",
                    "Fill an area of your own: Fill empty space puts the selection into the free part of the monitor (not under your other windows), Fill selection span fills exactly the space the selection covers, Fill area… lets you drag or type the rectangle. Choose Row, Column or Grid, a gap and the order; you see a preview with each window's action-bar verdict first – Apply, or Cancel (Esc).",
                    "In the toolbar, Swap when dropped is off, so windows can sit right next to or partly over each other; Snap, Grid and Lock aspect are there too. Ctrl+Z / Ctrl+Y undo and redo every step. The real windows follow when you let go.",
                ],
                "Leaving Custom with changes you have not saved under a name asks once: Save as… (a new layout of its own), Keep as draft (pick Custom later to get it back) or Discard (back to how it was). That is the only question; nothing else ever asks."),
            new(4, "Open your clients",
                "Press Start placing windows: from then on every WoW window that opens goes straight into the next free slot. Until you press it, the app leaves every window alone.",
                [
                    "Start placing windows (bottom right): the app places the squad's windows. Stop leaves them where they are – for example while you play something else.",
                    "Press \"Open N WoW clients\": one login screen per empty slot. You log in to each yourself.",
                    "Prefer Battle.net? Click \"Open Battle.net instead\" and press Play once per account.",
                    "Each slot shows Awaiting… until its window is there, then Connected. Wrong order? Drag the rows under \"Your squad\" by ⋮⋮, or open ▾ and press Identify.",
                    "All connected? The button becomes Re-arrange: it snaps every window back into its slot.",
                ],
                "WoWBoxer never logs in for you and never types anything. Windows of another WoW install (say Forever next to Classic Era) are left alone – \"Include\" adds them. Stop (or the tray menu) leaves all windows alone; the app also opens stopped."),
            new(SwitchingChapter, "Switch between windows",
                "Hover to activate is the WoWBoxer way to switch: the window under your mouse becomes active, so your next key press goes to that character. One key press = one action on one character.",
                [
                    "The switch below turns on hover to activate, done by WoWBoxer's KWin script between WoW windows only: while a WoW window is active, the WoW window under your mouse becomes active. Turning it on sets the delay to 30 ms, the default: windows do not activate while the mouse just passes over them. Move the slider for faster or slower; Reset to default puts 30 ms back.",
                    "With overlays, leave \"Bring window to front\" off and set the floating followers to \"Always on top\" (the pin in Your squad), so the main window can never cover them.",
                    "Closing WoWBoxer turns hover to activate off again (Settings can keep it on for the next start). It never moves focus to or from another application.",
                ],
                "Hover to activate is done by WoWBoxer's KWin script. It sends nothing to the game; WoWBoxer never sends keys or clicks. It is off until you turn it on.",
                hover: hover),
            new(SwapChapter, "Swap windows (advanced)",
                "Optional and off until you turn it on: hotkeys that move a character's window into the main slot – for example your healer's window into the big one. Only the windows move.",
                [
                    "Turn it on in Settings › Advanced › Window swap hotkeys. Main + side and Overlays use the large window as the main slot (one per party with 6–10 clients); it stays the main slot when the layout turns into Custom. Custom uses its clearly largest window. In Grid, or when the windows are about the same size, click the crown on one window per party on the map first – the crowns pulse if you press a swap hotkey without one.",
                    "Ctrl+Alt+Shift+Right / Left: the next / previous client into the main slot – closed windows are skipped, and after the last one it starts again. Ctrl+Alt+Shift+1 … 0: that client into the main slot. Ctrl+Alt+Shift+Backspace: everyone back home. Every hotkey can be changed or cleared in Settings.",
                    "The client that was in the main slot takes the chosen client's old place. Numbers, names and classes stay with the characters; size, monitor and \"always on top\" belong to the slot. Your squad shows who is in the main slot.",
                    "A swap never changes which window gets your keys: hover to activate or a click does that. Re-arrange, or Back to home layout in the tray, puts everyone back in their own slot.",
                ],
                SwapNote + " The hotkey itself goes to KWin, not to the game.",
                "Open Settings", openAdvanced),
            new(7, "If something looks wrong",
                "Most problems have a simple cause. The app tells you what it sees.",
                [
                    "Windows do not move: WoWBoxer needs KDE Plasma 6 (KWin) and loads its KWin script itself. Help › Diagnose shows whether the script runs.",
                    "Action bar ends cut off: the game picture is narrower than 4:3. Auto-fit action bar (on by default) sets one smaller UI Scale the next time all clients are closed. Or Settings › Fix action bar now, or ▾ › Shorten this window instead.",
                    "Still stuck: Help › Report a problem (also in the tray). It copies a short report and opens #setup-help in the WoWBoxer Discord – paste with Ctrl+V and press Enter. The full report is saved as a file you can drag in if someone asks.",
                ],
                "The Help tab lists the common problems with their fixes, and has Report a problem: a report with your user name, home folder, WoW account and character names hidden. You see all of it first; nothing is posted for you.",
                "Open Help", openHelp),
            new(8, "What WoWBoxer never does",
                "WoWBoxer only places and sizes your game windows. That is the whole job.",
                [
                    "Never sends keys or mouse clicks to the game – no broadcasting, no round robin.",
                    "Never reads game memory or injects anything.",
                    "Never logs in for you, and sends no telemetry.",
                ],
                "That is why it runs without root or any extra rights and is fully open source."),
        ];
        _current = Chapters[0];
        _current.IsCurrent = true;
        PickCommand = new RelayCommand(p =>
        {
            if (p is GuideChapter c) Current = c;
        });
    }

    public IReadOnlyList<GuideChapter> Chapters { get; }
    public ICommand PickCommand { get; }
    public string ChapterText => $"CHAPTER {Current.Number} OF {Chapters.Count}";

    public GuideChapter Current
    {
        get => _current;
        set
        {
            _current.IsCurrent = false;
            if (!Set(ref _current, value)) return;
            value.IsCurrent = true;
            OnPropertyChanged(nameof(ChapterText));
        }
    }
}
