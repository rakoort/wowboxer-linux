using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;
using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>The Diagnose report (same for wbx and the app): what KWin and /proc show, then one test placement per window.</summary>
public static class Diagnostics
{
    /// <param name="tryPlacement">False ("Report a problem"): only look – no window is moved.</param>
    public static async Task RunAsync(SquadPlan plan, IReadOnlyList<WindowInfo> open, IStatusSink log, string tool, bool tryPlacement = true)
    {
        log.Info($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   {tool}   {RuntimeInformation.FrameworkDescription}   {RuntimeInformation.OSDescription}");
        log.Info($"Session: {Env("XDG_SESSION_TYPE")}   desktop {Env("XDG_CURRENT_DESKTOP")}   KWin {await KWinVersionAsync()}");
        log.Info($"KWin script: running {(AgentLink.AgentVersion is { Length: > 0 } v ? v : "no answer")}   shipped {(AgentLink.ExpectedAgentVersion is { Length: > 0 } e ? e : "missing")}   last report {(AgentLink.LastReport is { } at ? $"{(DateTime.UtcNow - at).TotalSeconds:0.0} s ago" : "never")}");
        if (AgentLink.Problem is { } problem) log.Warn($"Link to KWin: {problem}");
        log.Info($"Path: {Environment.ProcessPath}");
        plan.Describe(log);

        log.Title("Processes (Battle.net / WoW / Agent):");
        foreach (var p in ProcessInfo.All().Where(p => p.WindowsPath.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!p.Name.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase) && !p.Name.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)
                && !p.Name.StartsWith("Agent", StringComparison.OrdinalIgnoreCase)) continue;
            log.Info($"  {p.Name}  pid {p.Pid}  started {ProcessInfo.StartTime((uint)p.Pid):HH:mm:ss}  {p.WindowsPath}");
            log.Detail($"      prefix {(p.Prefix.Length > 0 ? p.Prefix : "(WINEPREFIX not set)")}   exe {p.ExePath}{(File.Exists(p.ExePath) ? "" : "  (not found)")}");
        }

        log.Title("Windows KWin reports (class / title / frame / client / state):");
        foreach (var w in AgentLink.State.Report?.Windows ?? [])
        {
            var process = ProcessInfo.Inspect(w.Pid);
            log.Info($"  pid {w.Pid} {process?.Name ?? "?"}  class \"{w.ResourceClass}\"  title \"{w.Caption}\"  frame {w.Frame.ToPixels()}  client {w.Client.ToPixels()}"
                + $"  {(w.NoBorder ? "borderless" : "title bar")}{(w.KeepAbove ? "  keep above" : "")}{(w.Minimized ? "  minimized" : "")}{(w.FullScreen ? "  full screen" : "")}  output {w.Output}");
        }

        log.Title("WoW display settings (Config.wtf, read-only):");
        DescribeConfigs(open.Select(w => w.ExePath), log);
        foreach (var w in open)
        {
            var (cw, ch) = WindowFinder.ClientSizeOf(w.Handle);
            log.Info($"  window pid {w.Pid}: game renders {cw}x{ch}, {((w.Style & 0x00C00000) == 0 ? "borderless" : "title bar")}");
        }

        log.Title($"Game windows recognised (oldest first): {open.Count}");
        if (!tryPlacement)
        {
            foreach (var w in open) log.Info($"  pid {w.Pid}  {WindowFinder.VisibleRectOf(w.Handle)}");
            return;
        }
        var n = Math.Min(open.Count, plan.Slots.Count);
        for (var i = 0; i < n; i++)
        {
            var h = open[i].Handle;
            var target = plan.Slots[i].Rect;
            var before = WindowFinder.VisibleRectOf(h);
            if (plan.Profile.Options.Borderless) WindowPlacer.MakeBorderless(h);
            var error = WindowPlacer.TryMove(h, target);
            await Task.Delay(700);
            log.Info($"  {plan.Slots[i].Slot.Label}: before {before}  wanted {target}  after {WindowFinder.VisibleRectOf(h)}  move result {error}  (0 = asked KWin, -1 = window gone)");
        }
    }

    /// <summary>Display settings from each client install's Config.wtf (read-only).</summary>
    public static void DescribeConfigs(IEnumerable<string> exePaths, IStatusSink log)
    {
        foreach (var exe in exePaths.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var path = WowConfig.ConfigPathFor(exe);
            if (!File.Exists(path))
            {
                log.Warn($"  {path}: not found");
                continue;
            }
            var values = WowConfig.Parse(File.ReadAllText(path));
            log.Info($"  {path}  (shared by every client started from this folder)");
            foreach (var (name, meaning) in WowConfig.DisplayCvars)
                log.Info($"    {name,-24} {(values.TryGetValue(name, out var v) ? $"\"{v}\"" : "(not set)"),-14} {meaning}");
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "?";

    /// <summary>"KWin version: 6.6.5" from KWin's support information, "?" if KWin does not answer.</summary>
    private static async Task<string> KWinVersionAsync()
    {
        if (AgentLink.Connection is not { } c) return "?";
        try
        {
            var info = await Bus.CallAsync(c, "org.kde.KWin", "/KWin", "org.kde.KWin", "supportInformation", null, null, Bus.ReadString);
            var line = info.Split('\n').FirstOrDefault(l => l.StartsWith("KWin version:", StringComparison.Ordinal));
            return line?["KWin version:".Length..].Trim() ?? "?";
        }
        catch (DBusExceptionBase)
        {
            return "?";
        }
    }
}
