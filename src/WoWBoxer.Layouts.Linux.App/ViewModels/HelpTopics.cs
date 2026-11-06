namespace WoWBoxer.Layouts.App.ViewModels;

/// <summary>A common problem and what to do about it (Help tab).</summary>
public sealed record HelpTopic(string Problem, string Fix);

/// <summary>The Help tab's "Common problems": the things players actually run into, each with the fix.</summary>
public static class HelpTopics
{
    public static IReadOnlyList<HelpTopic> All { get; } =
    [
        new("Nothing happens when a WoW window opens",
            "The app is stopped (the title bar says so). Press Start placing windows, bottom right on Setup. Settings can start it whenever the app opens."),
        new("The windows do not move at all",
            "WoWBoxer moves windows through its KWin script, so it needs a KDE Plasma 6 session. It loads the script itself; Help › Diagnose shows whether it runs."),
        new("The layout changed after I moved a window",
            "While started, moving or resizing a WoW window by hand works like a drag on the map: the layout becomes Custom from exactly that (Ctrl+Z undoes it). Press Stop to move windows by hand without changing the layout."),
        new("The ends of the action bar are cut off",
            "The game picture is narrower than 4:3. Auto-fit action bar (on by default) sets a smaller UI Scale the next time all clients are closed. Or Settings › Fix action bar now, or ▾ › Shorten this window instead in Your squad."),
        new("Forever (or another client) maximises itself again",
            "It is in \"Windowed (Fullscreen)\". In game: Esc › Options › Graphics › Display Mode › Windowed, once. Then it can be placed."),
        new("The wrong character is in a window",
            "Drag the rows in Your squad by ⋮⋮ – the real windows follow. ▾ › Identify shows which window is which."),
        new("Some WoW windows are ignored",
            "The squad follows the WoW install with the most open windows; another game's windows are left alone. \"Include\" in Your squad lets them join (until the app restarts)."),
        new("Windows activate while the mouse just passes over them",
            "Raise the hover delay (Your squad or Settings), or press Reset to default for 30 ms. Hover to activate is done by WoWBoxer's KWin script; turning it off stops it at once."),
        new("The layout is suddenly on one monitor",
            "A monitor of the layout was unplugged or off, so the app went back to 1 monitor. Choose the monitors again under Monitors on Setup once it is back (or Ctrl+Z)."),
        new("A WoW client does not start",
            "Each client starts in its install's Wine prefix with Proton (umu-run). Start the game once by hand from that prefix (for example in Faugus Launcher or Lutris), then pick its Wow.exe under Settings."),
        new("Idle clients get disconnected after about 30 minutes",
            "The game does that to characters without input. WoWBoxer sends no input, ever – so it cannot keep them online."),
    ];
}
