using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>"Arrange now": places windows that are already open, one per slot.</summary>
public static class Arranger
{
    /// <param name="windows">Windows in slot order: windows[0] goes to slot 1. Extra windows are left alone.</param>
    /// <returns>True if every window ended up in place.</returns>
    public static Task<bool> ArrangeAsync(SquadPlan plan, IReadOnlyList<WindowInfo> windows, IStatusSink sink, CancellationToken cancel = default) =>
        ArrangeAsync(plan, windows.Take(plan.Slots.Count).Select((w, i) => (w, i)).ToList(), sink, cancel, Math.Max(0, windows.Count - plan.Slots.Count));

    /// <param name="placements">Each window with its slot index (slots may be left out: no window there yet).</param>
    /// <param name="extra">How many open windows have no slot (only reported).</param>
    public static async Task<bool> ArrangeAsync(SquadPlan plan, IReadOnlyList<(WindowInfo Window, int Slot)> placements, IStatusSink sink, CancellationToken cancel = default, int extra = 0)
    {
        placements = placements.Where(p => p.Slot >= 0 && p.Slot < plan.Slots.Count).ToList();
        if (placements.Count == 0)
        {
            sink.Warn("No World of Warcraft windows found. Start the games first (or run Diagnose).");
            return false;
        }
        var n = placements.Count;
        var handles = placements.Select(p => p.Window.Handle).ToList();
        var slots = placements.Select(p => p.Slot).ToList();
        var before = handles.Select(WindowFinder.VisibleRectOf).ToList();
        for (var i = 0; i < n; i++)
        {
            plan.Place(handles[i], slots[i]);
            sink.Ok($"Placing {plan.Slots[slots[i]].Slot.Label}  ({placements[i].Window.ProcessName}.exe, was {before[i]})");
        }
        await Task.Delay(1500, cancel);
        for (var i = 0; i < n; i++) plan.Place(handles[i], slots[i]);   // second pass: the game may have resized itself, or a frame was just added
        await Task.Delay(500, cancel);
        await Task.WhenAll(handles.Select((h, i) => plan.NudgeAsync(h, slots[i])));
        await Task.Delay(1000, cancel);

        var titled = new HashSet<(nint, int)>();
        var ok = true;
        for (var i = 0; i < n; i++)
        {
            if (plan.IsPlaced(handles[i], slots[i]))
            {
                plan.Retitle(handles[i], slots[i], titled);
                continue;
            }
            if (ok) plan.ExplainNotPlaced(sink, handles[i], slots[i], before[i]);   // explain the first one only
            ok = false;
        }
        if (extra > 0) sink.Warn($"{extra} extra WoW window(s) left as they are.");
        if (ok) sink.Ok("Done.");
        return ok;
    }
}
