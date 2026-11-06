using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux.Agent;

/// <summary>
/// What the app knows about KWin and what it wants from the agent: the last report, the numbered action queue and the
/// standing state (keep-above list, WoW windows, hover, own windows). Thread-safe: the D-Bus thread writes reports and
/// reads the desired state, the app thread places windows. No I/O here, so it is tested on its own.
/// </summary>
internal sealed class AgentState
{
    /// <summary>First handle given to a KWin window (far from 0 and from the sandbox's fake handles).</summary>
    private const long FirstHandle = 0x10000;

    private readonly Lock _gate = new();
    private readonly List<AgentAction> _pending = [];
    private readonly Dictionary<string, nint> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> _ids = [];
    private readonly HashSet<string> _keepAbove = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnWindow> _own = new(StringComparer.Ordinal);
    private long _nextAction = 1;
    private long _nextHandle = FirstHandle;
    private HoverDesired _hover = new(false, HoverSettings.DefaultDelayMs, true);

    public AgentState(string? epoch = null, int ownPid = 0)
    {
        Epoch = epoch ?? Guid.NewGuid().ToString("N");
        OwnPid = ownPid == 0 ? Environment.ProcessId : ownPid;
    }

    /// <summary>New on every app start: an agent that saw another epoch forgets its done counter.</summary>
    public string Epoch { get; }

    public int OwnPid { get; }

    /// <summary>The last report (null until the agent reported once).</summary>
    public AgentReport? Report { get; private set; }

    public DateTime? ReportedAt { get; private set; }

    /// <summary>The agent version from the last Pull or Report ("" until then).</summary>
    public string AgentVersion { get; private set; } = "";

    /// <summary>True after <see cref="Stop"/>: the agent gets an empty desired state and does nothing.</summary>
    public bool Stopped { get; private set; }

    // ---------- handles ----------

    /// <summary>The handle of a KWin window id. The same id keeps its handle for the life of the process; handles are never reused.</summary>
    public nint HandleOf(string id)
    {
        lock (_gate) return HandleOfLocked(id);
    }

    private nint HandleOfLocked(string id)
    {
        if (_handles.TryGetValue(id, out var h)) return h;
        h = (nint)_nextHandle++;
        _handles[id] = h;
        _ids[h] = id;
        return h;
    }

    /// <summary>The KWin window id of a handle, or null for a handle this process never gave out.</summary>
    public string? IdOf(nint handle)
    {
        lock (_gate) return _ids.GetValueOrDefault(handle);
    }

    // ---------- reports ----------

    /// <summary>Takes a report. Returns the hand edits it carries (handle, new frame) and the handles the agent gave up on.</summary>
    public (List<(nint Handle, PixelRect Frame)> HandEdits, List<nint> GaveUp) Apply(AgentReport report, DateTime now)
    {
        lock (_gate)
        {
            Report = report;
            ReportedAt = now;
            if (report.Agent.Length > 0) AgentVersion = report.Agent;
            Acknowledge(report.Epoch, report.Done);
            foreach (var w in report.Windows) HandleOfLocked(w.Id);
            var edits = report.HandEdited.Select(e => (HandleOfLocked(e.Id), e.Frame.ToPixels())).ToList();
            var gaveUp = report.GaveUp.Select(HandleOfLocked).ToList();
            return (edits, gaveUp);
        }
    }

    /// <summary>The window of a handle in the last report, or null if it is gone (or never existed).</summary>
    public AgentWindow? WindowOf(nint handle)
    {
        lock (_gate)
        {
            if (Report is null || !_ids.TryGetValue(handle, out var id)) return null;
            return Report.Windows.FirstOrDefault(w => w.Id == id);
        }
    }

    // ---------- actions ----------

    /// <summary>
    /// Queues an action and returns its number. A newer action of the same kind for the same window replaces a pending
    /// one (only the last place / above / border counts), so a busy app never floods the agent.
    /// </summary>
    public long Enqueue(string op, string id, PixelRect? rect = null, bool? on = null)
    {
        lock (_gate)
        {
            _pending.RemoveAll(a => a.Op == op && a.Id == id);
            var action = new AgentAction(_nextAction++, op, id) { Rect = rect is { } r ? AgentRect.From(r) : null, On = on };
            _pending.Add(action);
            Stopped = false;
            return action.N;
        }
    }

    /// <summary>Actions not yet confirmed by the agent, oldest first.</summary>
    public IReadOnlyList<AgentAction> Pending
    {
        get
        {
            lock (_gate) return _pending.ToList();
        }
    }

    /// <summary>The reply to Pull. Actions the agent confirmed for this epoch are dropped first.</summary>
    public DesiredState Desired(AgentHello hello)
    {
        lock (_gate)
        {
            if (hello.Agent.Length > 0) AgentVersion = hello.Agent;
            Acknowledge(hello.Epoch, hello.Done);
            if (Stopped)
                return new DesiredState(AgentJson.ProtocolVersion, Epoch, OwnPid, [], [], [], _hover with { Enabled = false }, []);
            return new DesiredState(AgentJson.ProtocolVersion, Epoch, OwnPid, _pending.ToList(),
                _keepAbove.Order(StringComparer.Ordinal).ToList(), _wow.Order(StringComparer.Ordinal).ToList(), _hover,
                _own.Values.OrderBy(o => o.Caption, StringComparer.Ordinal).ToList());
        }
    }

    private void Acknowledge(string? epoch, long done)
    {
        if (epoch == Epoch) _pending.RemoveAll(a => a.N <= done);
    }

    // ---------- standing state ----------

    /// <summary>Adds or removes a window from the keep-above list. True if the list changed.</summary>
    public bool SetKeepAbove(string id, bool above)
    {
        lock (_gate) return above ? _keepAbove.Add(id) : _keepAbove.Remove(id);
    }

    public bool IsKeptAbove(string id)
    {
        lock (_gate) return _keepAbove.Contains(id);
    }

    /// <summary>Replaces the set hover may switch between. True if it changed.</summary>
    public bool SetWow(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var next = ids.ToHashSet(StringComparer.Ordinal);
            if (next.SetEquals(_wow)) return false;
            _wow.Clear();
            _wow.UnionWith(next);
            return true;
        }
    }

    public HoverSettings Hover
    {
        get
        {
            lock (_gate) return new HoverSettings(_hover.Enabled, _hover.DelayMs, _hover.BringToFront);
        }
    }

    /// <summary>True if the hover entry changed.</summary>
    public bool SetHover(HoverSettings hover)
    {
        lock (_gate)
        {
            var next = new HoverDesired(hover.Enabled, Math.Max(hover.DelayMs, 0), hover.BringToFront);
            if (next == _hover) return false;
            _hover = next;
            return true;
        }
    }

    /// <summary>Shows (rect) or removes (null) one of the app's own windows by caption. True if the entry changed.</summary>
    public bool SetOwn(string caption, PixelRect? rect)
    {
        lock (_gate)
        {
            if (rect is not { } r) return _own.Remove(caption);
            var next = new OwnWindow(caption, AgentRect.From(r), Above: true, NoBorder: true, SkipTaskbar: true);
            if (_own.TryGetValue(caption, out var old) && old == next) return false;
            _own[caption] = next;
            return true;
        }
    }

    /// <summary>The app stops: every list is emptied, so the agent does nothing until something is asked again.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _pending.Clear();
            _keepAbove.Clear();
            _wow.Clear();
            _own.Clear();
            Stopped = true;
        }
    }
}
