using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// "Start squad": watches for WoW windows and places each new one in the next free slot (launch order).
/// Placement is re-applied for 30 s per window, because WoW resizes itself while loading. A closed window
/// frees its slot, so a restarted client fills it again.
/// </summary>
public sealed class SquadWatcher(SquadPlan plan, IStatusSink sink)
{
    public static readonly TimeSpan ReapplyTime = TimeSpan.FromSeconds(30);
    private const int MissesBeforeReport = 8;

    private readonly Dictionary<nint, int> _assigned = [];   // window -> slot index

    /// <summary>Raised (on the watcher's thread) when a window gets or loses a slot. Slot index -1 = closed.</summary>
    public event Action<nint, int>? AssignmentChanged;

    public IReadOnlyDictionary<nint, int> Assigned => _assigned;

    /// <param name="initialOrder">Windows that already have a slot (index = slot), e.g. the app's squad rows; others follow launch order.</param>
    /// <returns>True if all slots were filled and every window is in place.</returns>
    public async Task<bool> RunAsync(TimeSpan watchFor, CancellationToken cancel = default, IReadOnlyList<nint>? initialOrder = null)
    {
        if (initialOrder is not null)
            for (var i = 0; i < initialOrder.Count && i < plan.Slots.Count; i++)
                if (WindowFinder.Exists(initialOrder[i]) && !_assigned.ContainsKey(initialOrder[i])) _assigned[initialOrder[i]] = i;
        var placedAt = new Dictionary<nint, DateTime>();
        var before = new Dictionary<nint, PixelRect>();
        var misses = new Dictionary<nint, int>();
        var nudged = new HashSet<nint>();
        var titled = new HashSet<(nint, int)>();
        var reported = false;
        var deadline = DateTime.UtcNow + watchFor;
        var lastPlaced = DateTime.UtcNow;
        var firstRound = true;
        foreach (var h in _assigned.Keys)
        {
            placedAt[h] = DateTime.UtcNow;
            before[h] = WindowFinder.VisibleRectOf(h);
            misses[h] = 0;
            AssignmentChanged?.Invoke(h, _assigned[h]);
        }

        while (DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
        {
            foreach (var h in _assigned.Keys.Where(h => !WindowFinder.Exists(h)).ToList())
            {
                sink.Warn($"{plan.Slots[_assigned[h]].Slot.Label} window closed - slot is free again.");
                _assigned.Remove(h);
                placedAt.Remove(h);
                nudged.Remove(h);
                AssignmentChanged?.Invoke(h, -1);
            }

            foreach (var w in WindowFinder.FindGameWindows(plan.Game))
            {
                if (_assigned.ContainsKey(w.Handle)) continue;
                var free = Enumerable.Range(0, plan.Slots.Count).FirstOrDefault(s => !_assigned.ContainsValue(s), -1);
                if (free < 0) continue;
                _assigned[w.Handle] = free;
                placedAt[w.Handle] = lastPlaced = DateTime.UtcNow;
                before[w.Handle] = WindowFinder.VisibleRectOf(w.Handle);
                misses[w.Handle] = 0;
                sink.Ok($"{(firstRound ? "Found an open window" : "New WoW window")} -> {plan.Slots[free].Slot.Label}   ({w.ProcessName}.exe)");
                AssignmentChanged?.Invoke(w.Handle, free);
            }
            firstRound = false;

            foreach (var (h, slot) in _assigned)
            {
                var age = DateTime.UtcNow - placedAt[h];
                if (age >= ReapplyTime)
                {
                    if (nudged.Add(h)) await plan.NudgeAsync(h, slot);   // once, after loading settled
                    continue;
                }
                var ok = plan.IsPlaced(h, slot);   // checked before this pass's move: the last move had 1 s to land
                if (ok) plan.Retitle(h, slot, titled);
                plan.Place(h, slot);
                if (age.TotalSeconds >= 3 && !reported)
                {
                    misses[h] = ok ? 0 : misses[h] + 1;
                    if (misses[h] >= MissesBeforeReport)
                    {
                        reported = true;
                        plan.ExplainNotPlaced(sink, h, slot, before[h]);
                    }
                }
            }

            if (_assigned.Count >= plan.Slots.Count && DateTime.UtcNow - lastPlaced > ReapplyTime) break;
            try
            {
                await Task.Delay(1000, cancel);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        var allOk = _assigned.Count >= plan.Slots.Count;
        foreach (var (h, slot) in _assigned)
        {
            if (plan.IsPlaced(h, slot)) continue;
            allOk = false;
            sink.Warn($"{plan.Slots[slot].Slot.Label} is not in place: {WindowFinder.VisibleRectOf(h)}");
        }
        if (allOk)
            sink.Ok("All windows are in place. Have fun!");
        else if (_assigned.Count < plan.Slots.Count)
            sink.Warn($"Stopped watching ({_assigned.Count} of {plan.Slots.Count} windows found). If WoW is open, run Diagnose.");
        return allOk;
    }

    public static string BattleNetInstructions(bool forever) => forever
        ? "In Battle.net: pick \"World of Warcraft\", set the Game Version menu above Play to \"World of Warcraft: Forever\"."
        : "In Battle.net: pick \"World of Warcraft Classic\", set the version dropdown to \"Classic Era\".";

    /// <summary>Starts Battle.net if it is not running. Never logs in or presses anything.</summary>
    public static void EnsureBattleNet(IStatusSink sink, string? configuredExe, bool toolIsAdmin)
    {
        if (Sandbox.Active || BattleNet.IsRunning())
        {
            sink.Info("Battle.net is already running.");
            return;
        }
        if (BattleNet.FindExe(configuredExe) is not { } exe)
        {
            sink.Warn("Battle.net.exe not found - start it yourself (or set its path).");
            return;
        }
        if (toolIsAdmin)
            sink.Warn("This tool runs as administrator, so Battle.net and WoW would too. Better: run it without admin rights.");
        sink.Info("Starting Battle.net ...");
        BattleNet.Start(exe);
    }
}
