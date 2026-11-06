using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Keeps "always on top" true to the layout. The agent does the fast part itself: windows on its keep-above list get
/// keep-above back at once when Wine clears it. This guard keeps that list equal to the slots and checks every report
/// (window moved, restored, active window changed) for a window whose keep-above no longer matches its slot. Create it
/// on the UI thread: report callbacks are posted to the synchronization context it was created on.
/// </summary>
public sealed class ZOrderGuard : IDisposable
{
    private static ZOrderGuard? _active;   // one guard per process, like upstream

    private readonly Func<IReadOnlyList<(nint Handle, ZOrder Z)>> _wanted;
    private readonly Action<nint, ZOrder>? _corrected;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<nint, DateTime> _lastFix = [];
    private bool _busy;
    private bool _listening;

    /// <param name="wanted">The open WoW windows and the z-order their slot wants (read on every report).</param>
    /// <param name="corrected">Called after a window's "on top" had to be put right (for the activity log).</param>
    public ZOrderGuard(Func<IReadOnlyList<(nint Handle, ZOrder Z)>> wanted, Action<nint, ZOrder>? corrected = null)
    {
        _wanted = wanted;
        _corrected = corrected;
        _active?.Dispose();
        _active = this;
        if (Sandbox.Active) return;   // demo mode: no real windows to watch
        AgentLink.StateChanged += OnReport;
        _listening = true;
    }

    /// <summary>How many times a window's "on top" had to be put right since start (for tests and Diagnose).</summary>
    public int Corrections { get; private set; }

    public bool IsListening => _listening;

    /// <summary>
    /// Checks every slot now and corrects what differs. Returns how many windows were corrected. <paramref name="ours"/>:
    /// the change comes from the app itself (a toggle, a reorder), so it is not reported as a correction.
    /// </summary>
    public int Reassert(bool ours = false)
    {
        if (_busy) return 0;
        _busy = true;
        try
        {
            var fixedNow = 0;
            foreach (var (h, z) in _wanted())
            {
                // Our change lands with the agent's next report: give it a moment before correcting the same window again.
                if (!ours && _lastFix.TryGetValue(h, out var at) && DateTime.UtcNow - at < TimeSpan.FromMilliseconds(400)) continue;
                if (!WindowPlacer.ApplyZOrder(h, z)) continue;
                _lastFix[h] = DateTime.UtcNow;
                fixedNow++;
                if (ours) continue;
                Corrections++;
                _corrected?.Invoke(h, z);
            }
            return fixedNow;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnReport()
    {
        if (_context is null) Reassert();
        else _context.Post(_ =>
        {
            if (_listening) Reassert();
        }, null);
    }

    public void Dispose()
    {
        if (_listening) AgentLink.StateChanged -= OnReport;
        _listening = false;
        if (_active == this) _active = null;
    }
}
