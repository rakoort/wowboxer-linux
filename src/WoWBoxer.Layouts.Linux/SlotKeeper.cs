using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// The app's "always watching": which open WoW window sits in which slot. A new window takes the first free slot and is
/// placed again every step for 30 s (WoW resizes itself while loading), then nudged once. A closed window frees its
/// slot; the others keep theirs. The caller decides which windows belong to the squad and when to step.
/// </summary>
public sealed class SlotKeeper
{
    private readonly Dictionary<nint, int> _slots = [];          // window -> slot index
    private readonly Dictionary<nint, DateTime> _since = [];     // when the window got its slot (placing runs 30 s from here)
    private readonly Dictionary<nint, PixelRect> _before = [];
    private readonly Dictionary<nint, int> _misses = [];
    private readonly HashSet<nint> _nudged = [];
    private readonly HashSet<(nint, int)> _titled = [];
    private readonly HashSet<nint> _explained = [];

    public IReadOnlyDictionary<nint, int> Slots => _slots;

    /// <summary>The window in a slot, or 0.</summary>
    public nint WindowIn(int slot) => _slots.FirstOrDefault(kv => kv.Value == slot).Key;

    /// <summary>True while a window is still being placed (its first 30 s in the slot).</summary>
    public bool IsSettling(nint h) => _since.TryGetValue(h, out var t) && DateTime.UtcNow - t < SquadWatcher.ReapplyTime;

    /// <summary>
    /// Brings the mapping up to date. Closed windows free their slot; slots past <paramref name="slotCount"/> are given up;
    /// new windows take the first free slot – or the slot they already sit in exactly (<paramref name="sitsIn"/>), so
    /// windows arranged earlier keep their places.
    /// </summary>
    /// <param name="windows">The squad's open windows, oldest first (launch order decides the next free slot).</param>
    /// <returns>Windows that got a slot and windows that lost theirs (closed or past the slot count), with the slot.</returns>
    public (List<(nint Window, int Slot)> Added, List<(nint Window, int Slot)> Removed) Sync(IReadOnlyList<WindowInfo> windows, int slotCount, Func<nint, int>? sitsIn = null)
    {
        var open = windows.Select(w => w.Handle).ToHashSet();
        var removed = new List<(nint, int)>();
        foreach (var (h, slot) in _slots.Where(kv => !open.Contains(kv.Key) || kv.Value >= slotCount).ToList())
        {
            Forget(h);
            removed.Add((h, slot));
        }
        var added = new List<(nint, int)>();
        var fresh = windows.Where(w => !_slots.ContainsKey(w.Handle)).ToList();
        if (sitsIn is not null)
            foreach (var w in fresh.ToList())
            {
                var k = sitsIn(w.Handle);
                if (k < 0 || k >= slotCount || _slots.ContainsValue(k)) continue;
                Assign(w.Handle, k);
                added.Add((w.Handle, k));
                fresh.Remove(w);
            }
        foreach (var w in fresh)
        {
            var free = Enumerable.Range(0, slotCount).FirstOrDefault(s => !_slots.ContainsValue(s), -1);
            if (free < 0) break;   // more windows than slots: the rest are left alone
            Assign(w.Handle, free);
            added.Add((w.Handle, free));
        }
        return (added, removed);
    }

    /// <summary>
    /// One step: windows in their first 30 s are placed (again), then nudged once. A window that keeps refusing to move
    /// is explained once (admin rights, display mode).
    /// </summary>
    public async Task StepAsync(SquadPlan plan, IStatusSink sink)
    {
        foreach (var (h, slot) in _slots.ToList())
        {
            // (the mapping may change while a nudge is awaited: skip windows that left meanwhile)
            if (slot >= plan.Slots.Count || !_since.TryGetValue(h, out var since) || !WindowFinder.Exists(h)) continue;
            var age = DateTime.UtcNow - since;
            if (age >= SquadWatcher.ReapplyTime)
            {
                if (_nudged.Add(h)) await plan.NudgeAsync(h, slot);   // once, after loading settled
                continue;
            }
            var ok = plan.IsPlaced(h, slot);   // checked before this step's move: the last move had a moment to land
            if (ok) plan.Retitle(h, slot, _titled);
            plan.Place(h, slot);
            if (age.TotalSeconds < 3 || _explained.Contains(h)) continue;
            _misses[h] = ok ? 0 : _misses.GetValueOrDefault(h) + 1;
            if (_misses[h] >= 8 && WindowFinder.Exists(h) && _explained.Add(h)) plan.ExplainNotPlaced(sink, h, slot, _before.GetValueOrDefault(h));
        }
    }

    /// <summary>Places every window again for 30 s (after a pause, or "Re-arrange").</summary>
    public void Settle()
    {
        foreach (var h in _slots.Keys.ToList()) Assign(h, _slots[h]);
    }

    /// <summary>Squad reorder: the window in slot <paramref name="from"/> moves to <paramref name="to"/>, the ones between shift by one.</summary>
    public void Move(int from, int to, int slotCount)
    {
        var order = Enumerable.Range(0, slotCount).Select(WindowIn).ToList();
        var moving = order[from];
        order.RemoveAt(from);
        order.Insert(to, moving);
        Restore(order.Select((h, i) => (h, i)).Where(p => p.h != 0).ToDictionary(p => p.h, p => p.i));
    }

    /// <summary>Puts back a mapping (undo). Windows that closed meanwhile are skipped.</summary>
    public void Restore(IReadOnlyDictionary<nint, int> slots)
    {
        var keep = _slots.Keys.ToList();
        _slots.Clear();
        foreach (var (h, slot) in slots)
            if (keep.Contains(h)) _slots[h] = slot;
        foreach (var h in keep.Where(h => !_slots.ContainsKey(h))) Forget(h);
        _titled.Clear();   // titles follow the slot's name
    }

    private void Assign(nint h, int slot)
    {
        _slots[h] = slot;
        _since[h] = DateTime.UtcNow;
        _before[h] = WindowFinder.VisibleRectOf(h);
        _misses[h] = 0;
        _nudged.Remove(h);
        _explained.Remove(h);
    }

    private void Forget(nint h)
    {
        _slots.Remove(h);
        _since.Remove(h);
        _before.Remove(h);
        _misses.Remove(h);
        _nudged.Remove(h);
        _explained.Remove(h);
        _titled.RemoveWhere(t => t.Item1 == h);
    }
}
