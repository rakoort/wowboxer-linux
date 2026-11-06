using System.Globalization;
using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Cli;

internal sealed class CliOptions
{
    public string Command { get; private set; } = "help";
    public PresetKind Preset { get; private set; } = PresetKind.Side;
    public int? Clients { get; private set; }
    public string? ProfilePath { get; private set; }
    public string? ShareCode { get; private set; }
    public string Monitor { get; private set; } = "primary";
    public double MinAspect { get; private set; } = 1.25;
    /// <summary>Null = keep what the profile says (presets: title bars on).</summary>
    public bool? Borderless { get; private set; }
    public bool ResizeNudge { get; private set; }
    public bool UseTaskbarArea { get; private set; }
    public IReadOnlyList<string> Names { get; private set; } = [];
    public IReadOnlyList<string> Accounts { get; private set; } = [];
    /// <summary>Only this game's windows (null: the layout's game for arrange/start, every game for info/snap/diagnose).</summary>
    public WowGame? Game { get; private set; }
    public bool StartBattleNet { get; private set; } = true;
    public string? BattleNetExe { get; private set; }
    public double WatchMinutes { get; private set; } = 10;
    public bool Json { get; private set; }
    public string? SavePath { get; private set; }
    public string? OutFolder { get; private set; }

    public const string Usage = """
        wbx - WoWBoxer (command line)
        Moves and resizes World of Warcraft windows. Never sends keys or mouse clicks to the game.

        Commands:
          wbx start      start Battle.net, then place each WoW window as it opens (you press Play yourself)
          wbx arrange    place the WoW windows that are open now, oldest first
          wbx diagnose   write a report (monitors, windows, processes, Wine prefixes, KWin script) and try one placement
          wbx info       show monitors, the planned slots and the open WoW windows (moves nothing)
          wbx layout     print the layout as JSON and as a WBX1: share code
          wbx restore    give the WoW windows their title bars and borders back (moves nothing)
          wbx capture    save where the WoW windows are now as a layout: wbx capture --save my.json
          wbx snap       save a picture of what each WoW window shows (read-only): wbx snap [--out FOLDER]

        Layout:
          --preset side|overlay|grid|free   built-in layout (default: side = main left, followers right; dual = side on 2 monitors)
          --clients N                       1-10 (default: start 3, arrange = number of open windows)
          --profile FILE.json               use a saved wowboxer-layout/1 file instead of a preset
          --code WBX1:...                   use a layout share code instead of a preset
          --monitor primary|N               monitor for the main window (default: primary)
          --min-aspect 1.25                 width/height below which WoW letterboxes (default 5:4)
          --taskbar                         use the whole screen, including the panel area
          --borderless                      remove title bars (off by default: WoW may draw its UI at the wrong size)
          --no-borderless                   keep title bars, also when the layout file says borderless
          --nudge                           shrink each window 2 px and back after placing (off by default)

        Names and start:
          --names A,B,C                     slot names; windows are retitled "WoW - A" etc.
          --accounts A,B,C                  Battle.net account labels, only shown in the instructions
          --game classic|forever            which game's windows (default: the layout's game; Classic Era)
          --no-battlenet                    do not start Battle.net
          --battlenet-exe PATH              Battle.net.exe (Linux path inside its Wine prefix) if it is not found automatically
          --watch-minutes 10                how long "start" watches for new windows
          --json                            (layout) print only the JSON
          --save FILE.json                  (capture, layout) write the layout to a file

        Needs a KDE Plasma 6 session: WoWBoxer's KWin script reports the windows and moves them.
        Logs: ~/.local/share/WoWBoxer/Layouts/logs
        Environment: WBX_GAME_EXE (game exe), WBX_PROTONPATH (Proton for umu-run), WBX_FAKE_WOW (caption prefix: test windows count as WoW)
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-')) o.Command = args[i++].ToLowerInvariant();

        string Next(string name) => i < args.Length ? args[i++] : throw new UsageException($"{name} needs a value.");

        while (i < args.Length)
        {
            var arg = args[i++];
            switch (arg.ToLowerInvariant())
            {
                case "--preset":
                    var p = Next(arg);
                    o.Preset = Enum.TryParse<PresetKind>(p, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
                        ? kind : throw new UsageException($"Unknown preset \"{p}\". Use side, overlay, grid, free or dual.");
                    break;
                case "--clients": o.Clients = ParseInt(arg, Next(arg)); break;
                case "--profile": o.ProfilePath = Next(arg); break;
                case "--code": o.ShareCode = Next(arg); break;
                case "--monitor": o.Monitor = Next(arg); break;
                case "--min-aspect": o.MinAspect = ParseDouble(arg, Next(arg)); break;
                case "--taskbar": o.UseTaskbarArea = true; break;
                case "--borderless": o.Borderless = true; break;
                case "--no-borderless" or "--title-bars": o.Borderless = false; break;
                case "--no-nudge": o.ResizeNudge = false; break;
                case "--nudge": o.ResizeNudge = true; break;
                case "--names": o.Names = SplitList(Next(arg)); break;
                case "--accounts": o.Accounts = SplitList(Next(arg)); break;
                case "--game":
                    var g = Next(arg).ToLowerInvariant();
                    o.Game = g switch
                    {
                        "classic" or "classicera" or "classic-era" or "era" => WowGame.ClassicEra,
                        "forever" => WowGame.Forever,
                        _ => throw new UsageException($"Unknown game \"{g}\". Use classic or forever."),
                    };
                    break;
                case "--no-battlenet": o.StartBattleNet = false; break;
                case "--battlenet-exe": o.BattleNetExe = Next(arg); break;
                case "--watch-minutes": o.WatchMinutes = ParseDouble(arg, Next(arg)); break;
                case "--json": o.Json = true; break;
                case "--save": o.SavePath = Next(arg); break;
                case "--out": o.OutFolder = Next(arg); break;
                case "-h" or "--help" or "/?": o.Command = "help"; break;
                default: throw new UsageException($"Unknown option \"{arg}\".");
            }
        }
        if (o.Clients is { } c && (c < Presets.MinClients || c > Presets.MaxClients))
            throw new UsageException($"--clients must be {Presets.MinClients}-{Presets.MaxClients}.");
        return o;
    }

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException($"{name}: \"{value}\" is not a number.");

    private static double ParseDouble(string name, string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : throw new UsageException($"{name}: \"{value}\" is not a positive number.");

    private static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}

internal sealed class UsageException(string message) : Exception(message);
