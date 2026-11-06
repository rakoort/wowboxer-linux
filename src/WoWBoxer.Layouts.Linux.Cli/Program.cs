using System.Globalization;
using WoWBoxer.Layouts.Cli;
using WoWBoxer.Layouts.Linux;

// Same number format on every PC, so reports read the same ("1.25", not "1,25").
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (UsageException e)
{
    Console.Error.WriteLine("wbx: " + e.Message);
    Console.Error.WriteLine("Run \"wbx help\" for the commands and options.");
    return 2;
}

if (options.Command is "help" or "-h" or "--help")
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}
if (options.Command is "version" or "--version")
{
    Console.WriteLine(Commands.Version);
    return 0;
}

var logName = options.Command == "diagnose" ? $"diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.txt" : "last-run.log";

using var log = new Log(logName);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // let "start" finish its summary
    cancel.Cancel();
};

if (!options.Json)   // --json output stays clean for piping into a file
{
    log.Line();
    log.Line($"WoWBoxer {Commands.Version} - {options.Command}", ConsoleColor.Yellow);
    log.Line("-----------------------------------------", ConsoleColor.DarkYellow);
}

// Windows and monitors come from WoWBoxer's KWin script, which reports to whoever owns the link.
switch (await AgentLink.StartAsync())
{
    case AgentLinkStart.OtherInstance:
        log.Error("The WoWBoxer app is running and owns the link to KWin. Use the app, or close it and run wbx again.");
        return 1;
    case AgentLinkStart.Unavailable:
        log.Error($"WoWBoxer needs a KDE Plasma 6 session. {AgentLink.Problem}");
        return 1;
}
if (AgentLink.Problem is { } problem && !options.Json) log.Warn(problem);
if (AgentLink.OldScriptLoaded && !options.Json)
    log.Warn($"The KWin script \"{AgentLink.OldScriptName}\" is loaded too and may move the same windows. Turn it off in System Settings › Window Management › KWin Scripts.");

try
{
    return options.Command switch
    {
        "arrange" => await Commands.ArrangeAsync(options, log),
        "start" => await Commands.StartAsync(options, log, cancel.Token),
        "diagnose" => await Commands.DiagnoseAsync(options, log),
        "info" => Commands.Info(options, log),
        "restore" => Commands.Restore(options, log),
        "capture" => Commands.Capture(options, log),
        "snap" => Commands.Snap(options, log),
        "layout" => Commands.Layout(options, log),
        _ => throw new UsageException($"Unknown command \"{options.Command}\"."),
    };
}
catch (UsageException e)
{
    log.Error(e.Message);
    log.Info("Run \"wbx help\" for the commands and options.");
    return 2;
}
catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
{
    log.Error(e.Message);
    return 1;
}
catch (Exception e)
{
    log.Error("ERROR: " + e.Message);
    log.Detail(e.ToString());
    return 1;
}
finally
{
    // let the agent finish what was asked (e.g. "restore"), then unload it
    await AgentLink.FlushAsync(TimeSpan.FromSeconds(3));
    await AgentLink.StopAsync();
}
