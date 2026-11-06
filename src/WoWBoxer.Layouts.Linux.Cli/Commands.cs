using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.Cli;

internal static class Commands
{
    public static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Show monitors, the planned slots and the open WoW windows. Moves nothing.</summary>
    public static int Info(CliOptions o, Log log)
    {
        var squad = Squad.Create(o, defaultClients: 3);
        squad.Describe(log);
        var open = WindowFinder.FindGameWindows(o.Game);
        log.Info($"WoW windows open right now: {open.Count}");
        foreach (var w in open)
        {
            var (cw, ch) = WindowFinder.ClientSizeOf(w.Handle);
            var borderless = (w.Style & 0x00C00000) == 0;   // KWin noBorder
            log.Info($"  {w.ProcessName}.exe  pid {w.Pid}  {GameDetection.DisplayName(w.Game)}  class \"{w.ClassName}\"  window {w.Rect}  visible {WindowFinder.VisibleRectOf(w.Handle)}  top-most {WindowPlacer.IsTopmost(w.Handle)}");
            log.Info($"      game renders {cw}x{ch} (aspect {(ch == 0 ? 0 : (double)cw / ch):F3})  {(borderless ? "borderless" : "title bar")}");
            log.Info($"      {w.ExePath}");
            log.Info($"      {BarLine(w, cw, ch)}");
        }
        return 0;
    }

    /// <summary>Gives every WoW window its title bar and borders back (undo borderless). Moves nothing.</summary>
    public static int Restore(CliOptions o, Log log)
    {
        var open = WindowFinder.FindGameWindows(o.Game);
        if (open.Count == 0)
        {
            log.Warn("No World of Warcraft windows found.");
            return 1;
        }
        foreach (var w in open)
        {
            WindowPlacer.RestoreFrame(w.Handle);
            log.Ok($"Title bar and borders back: {w.ProcessName}.exe at {w.Rect}");
        }
        return 0;
    }

    /// <summary>Saves where the WoW windows are now (oldest = slot 1) as a layout file. Moves nothing.</summary>
    public static int Capture(CliOptions o, Log log)
    {
        var open = WindowFinder.FindGameWindows(o.Game);
        if (open.Count == 0)
        {
            log.Warn("No World of Warcraft windows found. Arrange them first, then capture.");
            return 1;
        }
        var monitors = MonitorReader.GetMonitors();
        var borderless = open.All(w => (w.Style & 0x00C00000) == 0);   // no WS_CAPTION anywhere
        var name = o.SavePath is null ? "Captured" : Path.GetFileNameWithoutExtension(o.SavePath);
        var profile = ProfileCapture.FromWindows(open.Select(w => WindowFinder.VisibleRectOf(w.Handle)).ToList(), monitors, borderless, name);

        foreach (var (s, p) in profile.Slots.Zip(LayoutEngine.Resolve(profile, monitors)))
            log.Info($"{s.N}. {s.Label,-10} -> {p.Rect}  monitor {p.Monitor.Index}");
        log.Info($"Title bars: {(borderless ? "removed" : "kept")}");

        if (o.SavePath is not null)
        {
            File.WriteAllText(o.SavePath, ProfileJson.Serialize(profile));
            log.Ok($"Saved to {Path.GetFullPath(o.SavePath)}");
            log.Info($"Use it with: wbx arrange --profile \"{o.SavePath}\"   (or wbx start --profile ...)");
        }
        else
            log.Info("Add --save FILE.json to keep it.");
        log.Highlight("Share code:");
        Console.WriteLine(ProfileJson.ToShareCode(profile));
        return 0;
    }

    /// <summary>Saves a PNG of what each WoW window draws (read-only), oldest first: slot1.png, slot2.png ...</summary>
    public static int Snap(CliOptions o, Log log)
    {
        var open = WindowFinder.FindGameWindows(o.Game);
        if (open.Count == 0)
        {
            log.Warn("No World of Warcraft windows found.");
            return 1;
        }
        var folder = o.OutFolder ?? Path.Combine(Log.Folder, "snaps", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        for (var i = 0; i < open.Count; i++)
        {
            var shot = WindowCapture.Capture(open[i].Handle);
            if (shot is null)
            {
                log.Warn($"Window {i + 1}: could not capture ({WindowCapture.LastError}).");
                continue;
            }
            var path = Path.Combine(folder, $"slot{i + 1}.png");
            WindowCapture.SavePng(shot, path);
            log.Ok($"Window {i + 1}: {shot.Width}x{shot.Height} ({shot.Method}) -> {path}");
            log.Info($"   {BarLine(open[i], shot.Width, shot.Height)}");
        }
        return 0;
    }

    /// <summary>The action bar as this window really shows it: its game area and the UI Scale in its install's Config.wtf.</summary>
    private static string BarLine(WindowInfo w, int width, int height)
    {
        if (height == 0) return "action bar: unknown (no game area)";
        var scale = w.ExePath.Length > 0 ? UiScaleFix.Read(w.ExePath) : UiScaleSetting.Automatic;
        var effective = UiScale.EffectiveAspect((double)width / height, scale);
        var fix = effective >= 4.0 / 3 - 0.001 ? "" : $"  (fix: UI Scale {UiScale.ForFullBar((double)width / height):0.00} or lower)";
        return $"action bar: {UiScale.BarVerdict(effective)}  [UI Scale {scale.Display}]{fix}";
    }

    public static int Layout(CliOptions o, Log log)
    {
        var squad = Squad.Create(o, defaultClients: 3);
        if (o.Json)
        {
            Console.WriteLine(ProfileJson.Serialize(squad.Profile));
            return 0;
        }
        squad.Describe(log);
        log.Line();
        Console.WriteLine(ProfileJson.Serialize(squad.Profile));
        log.Line();
        log.Highlight("Share code:");
        Console.WriteLine(ProfileJson.ToShareCode(squad.Profile));
        return 0;
    }
    /// <summary>Place the WoW windows that are open now, oldest first (the launcher's Arrange mode).</summary>
    public static async Task<int> ArrangeAsync(CliOptions o, Log log)
    {
        var plan = Squad.Create(o, defaultClients: WindowFinder.FindGameWindows(o.Game ?? WowGame.ClassicEra).Count);
        var open = WindowFinder.FindGameWindows(plan.Game);   // only the layout's game: a Forever window never joins a Classic layout
        if (open.Count == 0)
        {
            log.Warn($"No {GameDetection.DisplayName(plan.Game)} windows found. Start the games first (or run \"wbx diagnose\").");
            return 1;
        }
        plan.Describe(log);
        log.Line();
        return await Arranger.ArrangeAsync(plan, open, log) ? 0 : 1;
    }

    /// <summary>Start Battle.net, then place each WoW window as it appears (the launcher's Start mode).</summary>
    public static async Task<int> StartAsync(CliOptions o, Log log, CancellationToken cancel)
    {
        var plan = Squad.Create(o, defaultClients: 3);
        plan.Describe(log);
        if (o.StartBattleNet) SquadWatcher.EnsureBattleNet(log, o.BattleNetExe, plan.ToolIsAdmin);

        log.Line();
        log.Title(SquadWatcher.BattleNetInstructions(plan.Game == WowGame.Forever));
        log.Title("Then choose the account and press Play - in this order:");
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var account = i < o.Accounts.Count ? o.Accounts[i] : $"account {i + 1}";
            log.Title($"   {i + 1}. {plan.Slots[i].Slot.Label}   ({account})");
        }
        log.Title("Each game window snaps into place as soon as it appears. Leave this window open (Ctrl+C stops).");
        log.Line();

        var ok = await new SquadWatcher(plan, log).RunAsync(TimeSpan.FromMinutes(o.WatchMinutes), cancel);
        return ok ? 0 : 1;
    }

    /// <summary>Write a report of what KWin and /proc show, then try one placement per game window.</summary>
    public static async Task<int> DiagnoseAsync(CliOptions o, Log log)
    {
        var open = WindowFinder.FindGameWindows(o.Game);
        var plan = Squad.Create(o, defaultClients: open.Count > 0 ? open.Count : 3);
        await Diagnostics.RunAsync(plan, open, log, $"wbx {Version}");
        log.Line();
        log.Ok($"Saved to {log.FilePath}");
        return 0;
    }
}
