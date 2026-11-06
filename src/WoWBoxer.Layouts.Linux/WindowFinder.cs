using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>A top-level window and the facts we use to recognise and place it.</summary>
/// <param name="ClassName">KWin's resource class (the X11 WM_CLASS of a Wine window, e.g. "wow.exe").</param>
/// <param name="Rect">The frame (title bar included) in KWin logical pixels.</param>
/// <param name="Style">
/// A Windows-style value so the shared code keeps working: <see cref="WindowFinder.FramedStyle"/> with a title bar,
/// <see cref="WindowFinder.BorderlessStyle"/> without (KWin's noBorder).
/// </param>
public sealed record WindowInfo(nint Handle, uint Pid, string ProcessName, string ClassName, string Title, PixelRect Rect, int Style)
{
    /// <summary>Linux path of the client exe inside its Wine prefix ("" if /proc does not tell us).</summary>
    public string ExePath { get; init; } = "";
    /// <summary>Which WoW game the window belongs to, from its install folder.</summary>
    public WowGame Game => GameDetection.GameOf(ExePath);
}

/// <summary>
/// Finds World of Warcraft windows among the windows the agent reports. A window is a WoW client when its process is a
/// <c>Wow*.exe</c> (Wine shows the Windows path in /proc) or a game exe inside a "World of Warcraft" folder.
/// <c>WBX_FAKE_WOW=&lt;caption prefix&gt;</c> makes windows whose caption starts with it count as Forever clients
/// (development without the game, like upstream's tools/fakewow).
/// </summary>
public static class WindowFinder
{
    private const int MinWidth = 300, MinHeight = 200;

    /// <summary>WS_OVERLAPPEDWINDOW | WS_VISIBLE: what a framed window "has" (WS_CAPTION = 0x00C00000 is set).</summary>
    public const int FramedStyle = 0x10CF0000;
    /// <summary>WS_POPUP | WS_VISIBLE: what a borderless window "has" (no WS_CAPTION).</summary>
    public const int BorderlessStyle = unchecked((int)0x90000000);

    /// <summary>Where <c>WBX_FAKE_WOW</c> windows pretend to live (gives them the Forever game).</summary>
    internal const string FakeExe = "/wbx-fake-wow/drive_c/Program Files (x86)/World of Warcraft/_forever_/Wow.exe";

    public static bool IsWowProcessName(string? name) => GameDetection.IsWowProcessName(name);

    /// <summary>Upstream's rule on a Windows path or a Linux path (inside a "World of Warcraft" folder, not a launcher …).</summary>
    public static bool IsWowPath(string? path) => GameDetection.IsWowPath(path?.Replace('/', '\\'));

    /// <summary>Every managed window KWin reports, in stacking order (bottom first).</summary>
    public static List<nint> TopWindows()
    {
        if (Sandbox.Active) return Sandbox.GameWindows().Select(w => w.Handle).ToList();
        var report = AgentLink.State.Report;
        return report is null ? [] : report.Windows.OrderBy(w => w.Stack).Select(w => AgentLink.State.HandleOf(w.Id)).ToList();
    }

    /// <summary>
    /// WoW game windows, oldest process first (= the order the user pressed Play). With <paramref name="game"/> only
    /// that game's clients (e.g. a Forever window is never pulled into a Classic Era layout).
    /// </summary>
    public static List<WindowInfo> FindGameWindows(WowGame? game = null)
    {
        if (Sandbox.Active) return Sandbox.GameWindows().Where(w => game is not { } g || w.Game == g).ToList();
        var report = AgentLink.State.Report;
        if (report is null) return [];
        var fake = Environment.GetEnvironmentVariable("WBX_FAKE_WOW");
        var self = Environment.ProcessId;
        var found = new List<(WindowInfo Window, DateTime Start)>();
        foreach (var w in report.Windows)
        {
            if (w.Pid == self || w.Pid <= 0) continue;   // never our own windows (app, Identify overlays)
            var isFake = fake is { Length: > 0 } && w.Caption.StartsWith(fake, StringComparison.Ordinal);
            if (!isFake && ProcessInfo.Inspect(w.Pid) is not { IsWow: true }) continue;
            var info = Describe(w, isFake);
            if (info.Rect.Width < MinWidth || info.Rect.Height < MinHeight) continue;
            if (game is { } g && info.Game != g) continue;
            found.Add((info, ProcessInfo.StartTime((uint)w.Pid) ?? DateTime.MaxValue));
        }
        return found.OrderBy(f => f.Start).ThenBy(f => f.Window.Handle).Select(f => f.Window).ToList();
    }

    public static WindowInfo Describe(nint h)
    {
        if (Sandbox.Active) return Sandbox.GameWindows().FirstOrDefault(w => w.Handle == h) ?? new WindowInfo(h, 0, "?", "", "", default, 0);
        if (AgentLink.State.WindowOf(h) is not { } w) return new WindowInfo(h, 0, "?", "", "", default, 0);
        var fake = Environment.GetEnvironmentVariable("WBX_FAKE_WOW");
        return Describe(w, fake is { Length: > 0 } && w.Caption.StartsWith(fake, StringComparison.Ordinal));
    }

    private static WindowInfo Describe(AgentWindow w, bool fake)
    {
        var process = ProcessInfo.Inspect(w.Pid);
        return new WindowInfo(AgentLink.State.HandleOf(w.Id), (uint)w.Pid, fake ? "Wow" : process?.Name ?? "?", w.ResourceClass, w.Caption,
            w.Frame.ToPixels(), w.NoBorder ? BorderlessStyle : FramedStyle) { ExePath = fake ? FakeExe : process?.ExePath ?? "" };
    }

    public static uint PidOf(nint h) => Sandbox.Active ? Describe(h).Pid : (uint)(AgentLink.State.WindowOf(h)?.Pid ?? 0);

    /// <summary>KWin's resource class of the window.</summary>
    public static string ClassOf(nint h) => Sandbox.Active ? Describe(h).ClassName : AgentLink.State.WindowOf(h)?.ResourceClass ?? "";

    /// <summary>The caption KWin shows (without its " &lt;2&gt;" suffix for equal titles).</summary>
    public static string TitleOf(nint h) => Sandbox.Active ? Describe(h).Title : AgentLink.State.WindowOf(h)?.Caption ?? "";

    /// <summary>The frame (title bar included). KWin has no invisible resize border, so this equals <see cref="VisibleRectOf"/>.</summary>
    public static PixelRect RectOf(nint h) => Sandbox.Active ? Sandbox.RectOf(h) : AgentLink.State.WindowOf(h)?.Frame.ToPixels() ?? default;

    /// <summary>The size the game actually renders at (inside title bar and borders).</summary>
    public static (int Width, int Height) ClientSizeOf(nint h)
    {
        if (Sandbox.Active) return (Sandbox.RectOf(h).Width, Sandbox.RectOf(h).Height);
        var client = AgentLink.State.WindowOf(h)?.Client.ToPixels() ?? default;
        return (client.Width, client.Height);
    }

    /// <summary>The window as the user sees it: KWin's frame geometry.</summary>
    public static PixelRect VisibleRectOf(nint h) => RectOf(h);

    /// <summary>True while KWin still reports the window.</summary>
    public static bool Exists(nint h) => Sandbox.Active ? Sandbox.IsFake(h) : AgentLink.State.WindowOf(h) is not null;
}
