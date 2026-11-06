using System.Diagnostics;
using System.Text.Json;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Wine prefixes and how to start a Windows program in one: <c>umu-run</c> with the environment faugus-launcher uses.
/// Read-only towards faugus-launcher: its config only tells which Proton a prefix uses and where prefixes are.
/// </summary>
public static class WinePrefixes
{
    /// <summary>umu-run's name for "the newest GE-Proton" (downloaded by umu when missing).</summary>
    public const string DefaultProton = "GE-Proton";

    /// <summary>The app's Proton setting (a folder or a umu name such as "GE-Proton"); empty = automatic.</summary>
    public static string? ProtonPathSetting { get; set; }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string FaugusFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "faugus-launcher");

    /// <summary>The Proton for a prefix: the setting, <c>WBX_PROTONPATH</c>, faugus-launcher's runner for that prefix, else GE-Proton.</summary>
    public static string ProtonFor(string prefix)
    {
        if (ProtonPathSetting is { Length: > 0 } setting) return setting;
        if (Environment.GetEnvironmentVariable("WBX_PROTONPATH") is { Length: > 0 } env) return env;
        return FaugusRunner(prefix) is { Length: > 0 } runner ? RunnerToProtonPath(runner) : DefaultProton;
    }

    /// <summary>
    /// faugus-launcher's runner name for a prefix: the <c>runner</c> of a game in games.json with that prefix, else
    /// <c>default-runner</c> from config.ini. Null when faugus-launcher is not set up.
    /// </summary>
    internal static string? FaugusRunner(string prefix)
    {
        var games = FaugusGames();
        var wanted = ProcessInfo.RealPath(prefix);
        var game = games.FirstOrDefault(g => ProcessInfo.RealPath(g.Prefix) == wanted && g.Runner.Length > 0);
        if (game is not null) return game.Runner;
        return FaugusConfig().GetValueOrDefault("default-runner");
    }

    /// <summary>A faugus-launcher runner name as PROTONPATH: a folder under Steam's compatibilitytools.d, or a umu name.</summary>
    internal static string RunnerToProtonPath(string runner)
    {
        if (runner.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase)) return runner.Contains("Latest", StringComparison.OrdinalIgnoreCase) ? DefaultProton : runner.Split(' ')[0];
        if (runner.StartsWith("UMU-Proton", StringComparison.OrdinalIgnoreCase)) return "UMU-Proton";
        foreach (var root in new[] { Path.Combine(Home, ".local", "share", "Steam", "compatibilitytools.d"), Path.Combine(Home, ".steam", "root", "compatibilitytools.d") })
            if (Directory.Exists(Path.Combine(root, runner))) return Path.Combine(root, runner);
        return DefaultProton;
    }

    internal sealed record FaugusGame(string Title, string Path, string Prefix, string Runner);

    /// <summary>games.json of faugus-launcher (empty when missing or unreadable).</summary>
    internal static List<FaugusGame> FaugusGames()
    {
        try
        {
            var file = System.IO.Path.Combine(FaugusFolder, "games.json");
            if (!File.Exists(file)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object).Select(g => new FaugusGame(
                Text(g, "title"), Text(g, "path"), Text(g, "prefix"), Text(g, "runner"))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>config.ini of faugus-launcher as key → value (quotes removed).</summary>
    internal static Dictionary<string, string> FaugusConfig()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = Path.Combine(FaugusFolder, "config.ini");
            if (!File.Exists(file)) return values;
            foreach (var line in File.ReadLines(file))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return values;
    }

    /// <summary>
    /// Wine prefixes worth searching for a game: running WoW clients' prefixes, faugus-launcher's games and default
    /// prefix folder, then <c>~/Games/*</c>, <c>~/Faugus/*</c> and <c>~/.wine</c>. Only folders with a drive_c.
    /// </summary>
    public static IReadOnlyList<string> Known()
    {
        var list = new List<string>();
        list.AddRange(ProcessInfo.WowClients().Select(p => p.Prefix));
        list.AddRange(FaugusGames().Select(g => g.Prefix));
        var roots = new List<string>();
        if (FaugusConfig().GetValueOrDefault("default-prefix") is { Length: > 0 } faugusRoot) roots.Add(faugusRoot);
        roots.Add(Path.Combine(Home, "Games"));
        roots.Add(Path.Combine(Home, "Faugus"));
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                list.AddRange(Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        list.Add(Path.Combine(Home, ".wine"));
        return list.Where(p => p.Length > 0).Select(ProcessInfo.RealPath).Distinct(StringComparer.Ordinal)
            .Where(p => Directory.Exists(Path.Combine(p, "drive_c"))).ToList();
    }

    /// <summary>
    /// Starts a Windows exe (Linux path inside a prefix) with <c>umu-run</c>, the way faugus-launcher does, without
    /// waiting for it. Throws <see cref="InvalidOperationException"/>
    /// when the exe is not inside a Wine prefix, <see cref="System.ComponentModel.Win32Exception"/> when umu-run is missing.
    /// </summary>
    public static Process Start(string exe)
    {
        var prefix = WineProcess.PrefixOf(exe) ?? throw new InvalidOperationException($"{exe} is not inside a Wine prefix (no drive_c in its path).");
        var start = new ProcessStartInfo("umu-run") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? prefix };
        start.ArgumentList.Add(exe);
        foreach (var (name, value) in UmuEnvironment(prefix, ProtonFor(prefix))) start.Environment[name] = value;
        return Process.Start(start) ?? throw new InvalidOperationException("umu-run did not start.");
    }

    /// <summary>The environment faugus-launcher gives umu-run (docs/linux/port-plan.md).</summary>
    public static IReadOnlyList<(string Name, string Value)> UmuEnvironment(string prefix, string protonPath) =>
    [
        ("WINEPREFIX", prefix),
        ("PROTONPATH", protonPath),
        ("GAMEID", "umu-default"),
        ("PROTON_ENABLE_WAYLAND", "0"),
        ("WINE_SIMULATE_WRITECOPY", "1"),
        ("UMU_USE_STEAM", "1"),
        ("UMU_CONTAINER_NSENTER", "1"),
    ];
}
