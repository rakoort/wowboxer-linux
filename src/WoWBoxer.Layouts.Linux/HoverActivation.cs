using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Hover to activate, done by WoWBoxer's KWin script between WoW windows only: while a WoW window is active, resting
/// the pointer on another WoW window for the delay makes that one active (and raises it when "bring to front" is on).
/// There is no system setting behind it, so the value lives in the agent's desired state; the app keeps its own
/// setting and writes it here at start. Off when the app quits. Sends nothing to any window.
/// </summary>
public static class HoverActivation
{
    public static HoverSettings Read() => Sandbox.Active ? Sandbox.Hover : AgentLink.State.Hover;

    /// <summary>Hands the values to the agent. Always true (kept for the shared code, where Windows may refuse).</summary>
    public static bool Write(HoverSettings wanted)
    {
        if (Sandbox.Active)
        {
            Sandbox.Hover = wanted;
            return true;
        }
        if (AgentLink.State.SetHover(wanted)) AgentLink.Trigger();
        return true;
    }
}
