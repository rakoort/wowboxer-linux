using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

public enum PlaceResult { Gone, AlreadyThere, Requested }

/// <summary>
/// Moves, resizes and restyles windows by queueing actions for the KWin agent (<c>place</c>, <c>above</c>,
/// <c>border</c>). KWin never activates a window for that, and nothing is sent to the game. Results show up in the
/// agent's next report (within about 50 ms).
/// </summary>
public static class WindowPlacer
{
    /// <summary>Asks the window to move into <paramref name="rect"/>. Does nothing if it is already there.</summary>
    public static PlaceResult Place(nint h, PixelRect rect, bool borderless, ZOrder z = ZOrder.Normal)
    {
        if (Sandbox.Active)
        {
            if (!Sandbox.IsFake(h)) return PlaceResult.Gone;
            var topChanged = Sandbox.SetTopmost(h, z == ZOrder.Top);
            if (Sandbox.RectOf(h) == rect) return topChanged ? PlaceResult.Requested : PlaceResult.AlreadyThere;
            Sandbox.Move(h, rect);
            return PlaceResult.Requested;
        }
        if (IdOf(h) is not { } id || !WindowFinder.Exists(h)) return PlaceResult.Gone;
        var styleChanged = borderless ? MakeBorderless(h) : AddFrame(h);
        // "Always on top" is applied every time, also when the window is already in place.
        var zChanged = ApplyZOrder(h, z);
        if (!styleChanged && IsAt(h, rect)) return zChanged ? PlaceResult.Requested : PlaceResult.AlreadyThere;
        AgentLink.State.Enqueue("place", id, rect);
        AgentLink.Trigger();
        return PlaceResult.Requested;
    }

    /// <summary>True if KWin keeps the window above others.</summary>
    public static bool IsTopmost(nint h) => Sandbox.Active ? Sandbox.IsTopmost(h) : AgentLink.State.WindowOf(h)?.KeepAbove ?? false;

    /// <summary>
    /// Makes the window's "keep above" match the slot: <see cref="ZOrder.Top"/> sets it and puts the window on the
    /// agent's keep-above list (the agent sets it again when Wine clears it), anything else clears it. Never activates
    /// the window. Returns true if a change was requested.
    /// </summary>
    public static bool ApplyZOrder(nint h, ZOrder z)
    {
        if (Sandbox.Active) return Sandbox.SetTopmost(h, z == ZOrder.Top);
        if (IdOf(h) is not { } id || AgentLink.State.WindowOf(h) is not { } w) return false;
        var top = z == ZOrder.Top;
        var listChanged = AgentLink.State.SetKeepAbove(id, top);
        if (w.KeepAbove == top)
        {
            if (listChanged) AgentLink.Trigger();
            return false;
        }
        AgentLink.State.Enqueue("above", id, on: top);
        AgentLink.Trigger();
        return true;
    }

    /// <summary>
    /// Shrinks the window 2 px and back, so WoW gets a fresh resize and redraws at the real size
    /// (fixes UI drawn at the old size, e.g. cut-off action bars). Candidate fix from the launcher.
    /// </summary>
    public static async Task NudgeAsync(nint h, PixelRect rect, CancellationToken cancel = default)
    {
        if (Sandbox.Active || IdOf(h) is not { } id || !WindowFinder.Exists(h)) return;
        AgentLink.State.Enqueue("place", id, rect with { Width = rect.Width - 2, Height = rect.Height - 2 });
        AgentLink.Trigger();
        await Task.Delay(250, cancel).ConfigureAwait(false);
        AgentLink.State.Enqueue("place", id, rect);
        AgentLink.Trigger();
    }

    /// <summary>Move for Diagnose. 0 = asked KWin, -1 = the window is gone (KWin moves it without a result code).</summary>
    public static int TryMove(nint h, PixelRect rect)
    {
        if (Sandbox.Active) return Sandbox.Move(h, rect) ? 0 : -1;
        if (IdOf(h) is not { } id || !WindowFinder.Exists(h)) return -1;
        AgentLink.State.Enqueue("place", id, rect);
        AgentLink.Trigger();
        return 0;
    }

    /// <summary>Removes title bar and borders (KWin noBorder). Returns true if a change was requested.</summary>
    public static bool MakeBorderless(nint h) => SetBorder(h, on: false);

    /// <summary>Puts title bar and borders back. Returns true if a change was requested.</summary>
    public static bool AddFrame(nint h) => SetBorder(h, on: true);

    /// <summary>Puts title bar and borders back and clears "keep above". Position and size stay as they are.</summary>
    public static bool RestoreFrame(nint h)
    {
        if (Sandbox.Active) return Sandbox.SetTopmost(h, false);
        if (IdOf(h) is not { } id || !WindowFinder.Exists(h)) return false;
        AgentLink.State.SetKeepAbove(id, false);
        AgentLink.State.Enqueue("border", id, on: true);
        AgentLink.State.Enqueue("above", id, on: false);
        AgentLink.Trigger();
        return true;
    }

    private static bool SetBorder(nint h, bool on)
    {
        if (Sandbox.Active || IdOf(h) is not { } id || AgentLink.State.WindowOf(h) is not { } w) return false;
        if (w.NoBorder != on) return false;   // noBorder == !on already
        AgentLink.State.Enqueue("border", id, on: on);
        AgentLink.Trigger();
        return true;
    }

    /// <summary>
    /// Shows one of our own windows (found by this process and <paramref name="caption"/>) as an always-on-top label
    /// without border or taskbar entry over <paramref name="rect"/> (Identify). The agent places it whenever it appears
    /// or the entry changes. Remove it with <see cref="RemoveOverlay"/>.
    /// </summary>
    public static void ShowAsOverlay(string caption, PixelRect rect)
    {
        if (Sandbox.Active) return;
        if (AgentLink.State.SetOwn(caption, rect)) AgentLink.Trigger();
    }

    /// <summary>Forgets an overlay shown with <see cref="ShowAsOverlay"/> (call it when the window closes).</summary>
    public static void RemoveOverlay(string caption)
    {
        if (Sandbox.Active) return;
        if (AgentLink.State.SetOwn(caption, null)) AgentLink.Trigger();
    }

    /// <summary>True if the window's frame is at <paramref name="rect"/>.</summary>
    public static bool IsAt(nint h, PixelRect rect) => WindowFinder.VisibleRectOf(h) == rect;

    /// <summary>
    /// Sets the window title (e.g. "WoW - Paladin"): <c>_NET_WM_NAME</c> and <c>WM_NAME</c> of the game's XWayland
    /// window, found by its <c>_NET_WM_PID</c>, with <c>xprop</c>. Gives up after 1 s.
    /// </summary>
    public static bool SetTitle(nint h, string title)
    {
        if (Sandbox.Active) return Sandbox.IsFake(h);
        return AgentLink.State.WindowOf(h) is { } w && Xprop.SetTitle(w.Pid, w.Caption, title);
    }

    private static string? IdOf(nint h) => AgentLink.State.IdOf(h);
}
