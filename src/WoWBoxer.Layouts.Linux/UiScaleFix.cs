using WoWBoxer.Layouts.Core;

namespace WoWBoxer.Layouts.Linux;

/// <summary>
/// Writes WoW's UI Scale into the install's shared <c>WTF/Config.wtf</c> (inside the Wine prefix) – only while no client
/// of that install runs, because every client rewrites the file when it exits. Changes nothing else in the file and
/// keeps a one-time backup (<c>Config.wtf.wowboxer-backup</c>). Never touches a running game.
/// </summary>
public static class UiScaleFix
{
    public const string BackupSuffix = ".wowboxer-backup";

    /// <summary>Running game clients whose exe lives in <paramref name="installFolder"/> (from /proc).</summary>
    public static int RunningClients(string installFolder)
    {
        if (Sandbox.Active) return Sandbox.GameWindows().Count;
        var folder = Canonical(installFolder);
        return ProcessInfo.WowClients().Count(p => Path.GetDirectoryName(p.ExePath) is { } dir && Canonical(dir) == folder);
    }

    /// <summary>A folder path with the Wine prefix's symlinks resolved, so umu's <c>pfx</c> path equals the real one.</summary>
    private static string Canonical(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('/');
        return WineProcess.PrefixOf(full) is { } prefix ? ProcessInfo.RealPath(prefix) + full[prefix.Length..] : full;
    }

    public static UiScaleSetting Read(string exe) => Sandbox.Active ? Sandbox.UiScale : UiScale.ReadFile(WowConfig.ConfigPathFor(exe));

    /// <summary>Sets "Use UI Scale" and the scale. Returns null on success, otherwise why it did not write.</summary>
    public static string? Write(string exe, bool use, double scale)
    {
        if (Sandbox.Active)
        {
            Sandbox.UiScale = Sandbox.UiScale with { Use = use, Scale = Math.Clamp(scale, UiScale.Min, UiScale.Max) };
            return null;
        }
        var folder = Path.GetDirectoryName(exe) ?? "";
        var running = RunningClients(folder);
        if (running > 0) return $"{running} client(s) of this WoW install are running. WoW rewrites Config.wtf when it closes, so close them first.";
        var path = WowConfig.ConfigPathFor(exe);
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var backup = path + BackupSuffix;
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            var updated = UiScale.WithScale(text, use, Math.Clamp(scale, UiScale.Min, UiScale.Max));
            if (updated != text) File.WriteAllText(path, updated);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not write {path}: {e.Message}";
        }
    }
}
