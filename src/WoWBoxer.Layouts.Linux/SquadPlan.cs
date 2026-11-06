using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>A profile resolved to pixels on the current monitors, plus the placement steps every flow shares.</summary>
public sealed class SquadPlan
{
    public LayoutProfile Profile { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public IReadOnlyList<PlacedSlot> Slots { get; }
    /// <summary>Only this game's windows are placed (from the layout).</summary>
    public WowGame Game => Profile.GameOrDefault;
    /// <summary>Always false on Linux (kept for the shared code: no administrator split to explain).</summary>
    public bool ToolIsAdmin { get; } = ProcessInfo.CurrentElevation() == Elevation.Elevated;
    /// <summary>Retitle placed windows "WoW - Name" for slots that have a name.</summary>
    public bool Rename { get; }

    public SquadPlan(LayoutProfile profile, IReadOnlyList<MonitorInfo> monitors, bool rename)
    {
        Profile = profile;
        Monitors = monitors;
        Slots = LayoutEngine.Resolve(profile, monitors);
        Rename = rename;
    }

    public static SquadPlan ForCurrentMonitors(LayoutProfile profile, bool rename)
    {
        var monitors = MonitorReader.GetMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("KWin reports no monitors (is the WoWBoxer KWin script running?).");
        return new SquadPlan(profile, monitors, rename);
    }

    public void Describe(IStatusSink sink)
    {
        foreach (var m in Monitors)
            sink.Report(StatusLevel.Title, $"Monitor {m.Index}{(m.IsPrimary ? " (primary)" : "")}  {m.DeviceName}  screen {m.Bounds}  work area {m.WorkArea}  scale {m.ScalePercent} %");
        sink.Info($"Layout \"{Profile.Name}\"  min aspect {Profile.Options.MinAspect}  borderless {Profile.Options.Borderless}  taskbar area {Profile.Options.UseTaskbarArea}");
        foreach (var s in Slots)
        {
            var z = s.Slot.Z == ZOrder.Top ? "  on top" : "";
            var fallback = s.MonitorFallback ? $"  (monitor {s.Slot.Monitor} not found - using primary)" : "";
            sink.Info($"{s.Slot.N}. {s.Slot.Label,-10} -> {s.Rect}  monitor {s.Monitor.Index}{z}{fallback}");
        }
    }

    public PlaceResult Place(nint h, int slot) =>
        WindowPlacer.Place(h, Slots[slot].Rect, Profile.Options.Borderless, Slots[slot].Slot.Z);

    public bool IsPlaced(nint h, int slot) => WindowPlacer.IsAt(h, Slots[slot].Rect);

    /// <summary>The optional 2 px nudge; afterwards the window is put back to its exact size if the restore got lost.</summary>
    public async Task NudgeAsync(nint h, int slot)
    {
        if (!Profile.Options.ResizeNudge) return;
        await WindowPlacer.NudgeAsync(h, Slots[slot].Rect);
        await Task.Delay(400);
        if (!IsPlaced(h, slot)) Place(h, slot);
    }

    /// <summary>Retitles a placed window once ("WoW - Paladin") when renaming is on and the slot has a name.</summary>
    public void Retitle(nint h, int slot, HashSet<(nint, int)> done)
    {
        if (!Rename || Slots[slot].Slot.Name is not { Length: > 0 } name || !done.Add((h, slot))) return;
        WindowPlacer.SetTitle(h, "WoW - " + name);
    }

    /// <summary>Explains why a window did not move.</summary>
    public void ExplainNotPlaced(IStatusSink sink, nint h, int slot, PixelRect before)
    {
        var now = WindowFinder.VisibleRectOf(h);
        sink.Warn($"{Slots[slot].Slot.Label} window did not move into place.");
        sink.Detail($"   wanted {Slots[slot].Rect}   is {now}   was {before}   KWin script {(AgentLink.AgentVersion is { Length: > 0 } v ? v : "not answering")}");
        if (AgentLink.LastReport is not { } at || DateTime.UtcNow - at > TimeSpan.FromSeconds(15))
            sink.Warn("WoWBoxer's KWin script does not answer. Restart WoWBoxer; if that does not help, run Diagnose.");
        else if (now != before)
            sink.Warn("The window moved, but the game changed its size again. In WoW set Display Mode = Windowed.");
        else
            sink.Warn("Run Diagnose with the games open and send the report.");
    }
}
