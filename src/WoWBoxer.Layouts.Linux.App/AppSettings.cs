using System.IO;
using System.Text.Json;
using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.App;

/// <summary>Small per-user settings in ~/.config/WoWBoxer/Layouts/settings.json (XDG config). Nothing leaves the PC.</summary>
public sealed class AppSettings
{
    public static string Folder => Demo.On ? Path.Combine(Demo.Root, "roaming") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WoWBoxer", AppFlavor.DataFolder);
    public static string ProfilesFolder => Path.Combine(Folder, "profiles");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public bool FirstRunDone { get; set; }
    public string? LastProfile { get; set; }
    /// <summary>The version that last ran; a newer one shows "What's new" once.</summary>
    public string? LastSeenVersion { get; set; }
    public bool RenameWindows { get; set; } = true;
    /// <summary>Ids of the experimental features the user switched on (Settings › Experimental). Off unless listed.</summary>
    public List<string> Experimental { get; set; } = [];
    /// <summary>Start placing windows when the app opens. Off: it opens stopped and leaves every window alone until Start.</summary>
    public bool StartPlacingOnOpen { get; set; }
    public bool Animations { get; set; } = true;
    public string LastTab { get; set; } = "Setup";
    public string? BattleNetExe { get; set; }
    /// <summary>The last used WoW client exe (any install); updated from the open clients, found automatically when empty.</summary>
    public string? GameExe { get; set; }
    /// <summary>Set WoW's UI Scale automatically (while the clients are closed) so the whole action bar fits.</summary>
    public bool AutoFitActionBar { get; set; } = true;
    /// <summary>WoW account folder per slot (index = slot - 1). Local only, never part of a shared layout.</summary>
    public List<string> Accounts { get; set; } = [];
    /// <summary>
    /// The Proton the clients start with (a folder or a umu name such as "GE-Proton"); null = automatic: WBX_PROTONPATH,
    /// else Faugus Launcher's choice for the prefix, else GE-Proton.
    /// </summary>
    public string? ProtonPath { get; set; }
    /// <summary>
    /// WoWBoxer's own hover-to-activate values (on Linux its KWin script does it; there is no desktop setting to change).
    /// Null = never set. They are given to the agent at start; hover ends when the app quits.
    /// </summary>
    public HoverSettings? Hover { get; set; }
    /// <summary>Turn hover to activate off when the app quits (on by default; the delay and bring-to-front stay). Off: it is on again at the next start.</summary>
    public bool TurnHoverOffOnExit { get; set; } = true;
    /// <summary>
    /// When the user last set a slot's name or class by hand (unix seconds, index = slot - 1). A character from the
    /// WoWBoxer AddOn only replaces them when the AddOn saved it later.
    /// </summary>
    public List<long> SlotEditedAt { get; set; } = [];

    /// <summary>Advanced editing on the "Your screens" map (toolbar, multi-selection).</summary>
    public bool AdvancedEditing { get; set; }
    /// <summary>In Advanced editing: dropping a window on another swaps them (off by default there; always on outside).</summary>
    public bool SwapOnDropAdvanced { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public bool ShowGrid { get; set; }
    public bool LockAspect { get; set; }
    /// <summary>Pixels between windows for Fill row / column and the area fills.</summary>
    public int FillGap { get; set; }
    /// <summary>Area fills: row, column or grid; grid rows / columns (0 = automatic); client-number order; typed areas in %.</summary>
    public TileArrangement FillArrangement { get; set; } = TileArrangement.Grid;
    public int FillRows { get; set; }
    public int FillColumns { get; set; }
    public bool FillByNumber { get; set; }
    public bool FillAreaPercent { get; set; }

    /// <summary>Window swap hotkeys (Settings › Advanced). Off by default; only moves windows, never sends input.</summary>
    public bool SwapHotkeys { get; set; }
    /// <summary>Hotkey text per swap action ("Ctrl+Alt+Shift+Right"); missing = the default, "" = no hotkey.</summary>
    public Dictionary<string, string> SwapKeys { get; set; } = [];
    /// <summary>Which clients next / previous walk through.</summary>
    public SwapCycleScope SwapScope { get; set; } = SwapCycleScope.Party1;
    /// <summary>Custom next/previous order (1-based client numbers, may repeat); null = number order.</summary>
    public List<int>? SwapOrder { get; set; }
    /// <summary>Keep swapped positions after a restart (off: the app starts in the home layout).</summary>
    public bool SwapRemember { get; set; }
    /// <summary>The last placement per layout name (client → slot). Always kept, so swapped windows are recognised after a restart.</summary>
    public Dictionary<string, int[]> SwapPositions { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience; never crash over them
        }
    }
}
