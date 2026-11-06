using WoWBoxer.Layouts.Core;
using WoWBoxer.Layouts.Linux;

namespace WoWBoxer.Layouts.Cli;

/// <summary>Builds the squad plan for this run from the command-line options.</summary>
internal static class Squad
{
    public static SquadPlan Create(CliOptions o, int defaultClients)
    {
        var monitors = MonitorReader.GetMonitors();
        if (monitors.Count == 0) throw new InvalidOperationException("KWin reports no monitors (is the WoWBoxer KWin script running?).");

        LayoutProfile profile;
        if (o.ProfilePath is not null)
            profile = ProfileJson.Deserialize(File.ReadAllText(o.ProfilePath));
        else if (o.ShareCode is not null)
            profile = ProfileJson.FromShareCode(o.ShareCode);
        else
        {
            var options = new LayoutOptions { MinAspect = o.MinAspect, UseTaskbarArea = o.UseTaskbarArea, Borderless = o.Borderless ?? false, ResizeNudge = o.ResizeNudge };
            var clients = Math.Clamp(o.Clients ?? defaultClients, Presets.MinClients, Presets.MaxClients);
            try
            {
                profile = Presets.Create(o.Preset, clients, monitors, o.Monitor, options);
            }
            catch (ArgumentException e) when (e is not ArgumentOutOfRangeException)
            {
                throw new UsageException(e.Message);
            }
        }

        // Command-line switches win over the file, so a saved layout can be tried with or without borders etc.
        profile = profile with
        {
            Options = profile.Options with
            {
                Borderless = o.Borderless ?? profile.Options.Borderless,
                ResizeNudge = o.ResizeNudge,
            },
            Game = o.Game ?? profile.Game ?? WowGame.ClassicEra,
            Slots = profile.Slots.Select((s, i) => i < o.Names.Count ? s with { Name = o.Names[i] } : s).ToList(),
        };
        return new SquadPlan(profile, monitors, rename: o.Names.Count > 0);
    }
}
