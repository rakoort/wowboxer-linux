using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Demo / screenshot mode ("--demo"): made-up monitors, WoW windows, hover-to-activate and UI Scale values. While it
/// is active, every call that would read or change the real PC (windows, monitors, KWin, Config.wtf,
/// processes) uses these instead, so the app can be shown – and screenshotted – without touching anything real and
/// without any real user, account, character name or path. Battle.net and game clients are never started.
/// </summary>
public static class Sandbox
{
    /// <summary>Fake window handles start here (never sent to KWin while the sandbox is active).</summary>
    private const long FirstHandle = 0x5EED0000;

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, (WindowInfo Info, PixelRect Rect, bool Topmost)> Windows = [];
    private static IReadOnlyList<MonitorInfo> _monitors = [];

    public static bool Active { get; private set; }

    /// <summary>The demo windows' game exe: Battle.net's default folder in a made-up Wine prefix (no user name in it; never read or started).</summary>
    public const string GameExe = "/opt/wine-prefix/drive_c/Program Files (x86)/World of Warcraft/_classic_era_/WowClassic.exe";

    public const string BattleNetExe = "/opt/wine-prefix/drive_c/Program Files (x86)/Battle.net/Battle.net Launcher.exe";

    public static HoverSettings Hover { get; set; } = new(false, HoverSettings.DefaultDelayMs, true);

    public static UiScaleSetting UiScale { get; set; } = UiScaleSetting.Automatic;

    public static void Start(IReadOnlyList<MonitorInfo> monitors)
    {
        Active = true;
        _monitors = monitors;
    }

    public static IReadOnlyList<MonitorInfo> Monitors => _monitors;

    /// <summary>Exactly <paramref name="count"/> open demo WoW windows (stacked on the primary monitor until placed).</summary>
    public static void SetWindowCount(int count)
    {
        lock (Gate)
        {
            while (Windows.Count > count) Windows.Remove(Windows.Keys.Max());
            for (var i = Windows.Count; i < count; i++)
            {
                var h = (nint)(FirstHandle + i + 1);
                var rect = new PixelRect(100 + 30 * i, 100 + 30 * i, 1280, 800);
                var info = new WindowInfo(h, (uint)(9000 + i), "WowClassic", "waApplication Window", "World of Warcraft", rect, 0) { ExePath = GameExe };
                Windows[h] = (info, rect, false);
            }
        }
    }

    public static bool IsFake(nint h)
    {
        lock (Gate) return Windows.ContainsKey(h);
    }

    internal static List<WindowInfo> GameWindows()
    {
        lock (Gate) return Windows.OrderBy(w => w.Key).Select(w => w.Value.Info with { Rect = w.Value.Rect }).ToList();
    }

    internal static PixelRect RectOf(nint h)
    {
        lock (Gate) return Windows.TryGetValue(h, out var w) ? w.Rect : default;
    }

    internal static bool IsTopmost(nint h)
    {
        lock (Gate) return Windows.TryGetValue(h, out var w) && w.Topmost;
    }

    internal static bool Move(nint h, PixelRect rect)
    {
        lock (Gate)
        {
            if (!Windows.TryGetValue(h, out var w)) return false;
            Windows[h] = w with { Rect = rect };
            return true;
        }
    }

    internal static bool SetTopmost(nint h, bool top)
    {
        lock (Gate)
        {
            if (!Windows.TryGetValue(h, out var w) || w.Topmost == top) return false;
            Windows[h] = w with { Topmost = top };
            return true;
        }
    }
}
