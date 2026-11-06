using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux.Agent;

namespace WoWBoxer.Layouts.Linux;

/// <summary>The monitors as KWin reports them (the agent's <c>outputs</c>), in KWin logical pixels.</summary>
public static class MonitorReader
{
    /// <summary>All monitors in Plasma's screen order (index 1, 2 …); the first is the primary. Empty before the first report.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (Sandbox.Active) return Sandbox.Monitors;
        return AgentLink.State.Report is { } report ? FromOutputs(report.Outputs) : [];
    }

    internal static IReadOnlyList<MonitorInfo> FromOutputs(IReadOnlyList<AgentOutput> outputs) =>
        outputs.Select((o, i) => new MonitorInfo(i + 1, o.Name, i == 0, o.Geometry.ToPixels(), o.WorkArea.ToPixels(),
            (int)Math.Round(96 * (o.Scale > 0 ? o.Scale : 1))) { DeviceId = $"{o.Name}:{o.Model}" }).ToList();
}
