namespace WoWBoxer.Layouts.Linux;

/// <summary>Finds and starts the Battle.net app in its Wine prefix. The user still picks the account and presses Play.</summary>
public static class BattleNet
{
    private static readonly string[] ExeNames = ["Battle.net.exe", "Battle.net Launcher.exe"];

    public static bool IsRunning() =>
        ProcessInfo.All().Any(p => p.WindowsPath.Length > 0 && ExeNames.Contains(p.Name + ".exe", StringComparer.OrdinalIgnoreCase));

    /// <summary>The configured exe, else Battle.net in a known Wine prefix (the game's prefix first).</summary>
    public static string? FindExe(string? configured = null)
    {
        if (Sandbox.Active) return Sandbox.BattleNetExe;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var prefixes = new List<string>();
        if (GameInstall.FindExe(null) is { } game && WineProcess.PrefixOf(game) is { } gamePrefix) prefixes.Add(gamePrefix);
        prefixes.AddRange(WinePrefixes.Known());
        return prefixes.SelectMany(p => ExeNames.Select(n => Path.Combine(p, "drive_c", "Program Files (x86)", "Battle.net", n))).FirstOrDefault(File.Exists);
    }

    /// <summary>Starts Battle.net with umu-run in its prefix. Throws like <see cref="WinePrefixes.Start"/>.</summary>
    public static void Start(string exe) => WinePrefixes.Start(exe).Dispose();
}
