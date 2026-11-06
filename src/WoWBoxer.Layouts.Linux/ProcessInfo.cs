namespace WoWBoxer.Layouts.Linux;

/// <summary>Kept for the shared view models: on Linux a game never runs with more rights than WoWBoxer.</summary>
public enum Elevation { Normal, Elevated, Unknown }

/// <summary>Process facts from <c>/proc</c> (works for every process of the same user; no rights needed).</summary>
public static class ProcessInfo
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, (DateTime? Start, WineProcess? Process)> Cache = [];
    private static long _bootTime;

    /// <summary>The exe name without ".exe" ("Wow" for a WoW client), "?" if the process is gone.</summary>
    public static string Name(uint pid) => Inspect((int)pid)?.Name ?? "?";

    /// <summary>The Linux path of the exe (for a Wine program: inside its prefix's drive_c), "" if unknown.</summary>
    public static string ImagePath(uint pid) => Inspect((int)pid)?.ExePath ?? "";

    /// <summary>Process start time (UTC), used for "oldest window first". Null if the process is gone.</summary>
    public static DateTime? StartTime(uint pid)
    {
        var boot = Interlocked.Read(ref _bootTime);
        if (boot == 0)
        {
            boot = WineProcess.BootTimeFrom(ReadText("/proc/stat"));
            Interlocked.Exchange(ref _bootTime, boot);
        }
        var stat = ReadText($"/proc/{pid}/stat");
        return stat.Length == 0 ? null : WineProcess.StartTimeFrom(stat, boot);
    }

    /// <summary>Always <see cref="Elevation.Normal"/>: there is no administrator split to explain on Linux.</summary>
    public static Elevation ElevationOf(uint pid) => Elevation.Normal;

    public static Elevation CurrentElevation() => Elevation.Normal;

    /// <summary>
    /// One process as Wine sees it (cached per pid and start time, so a reused pid is read again). The prefix is
    /// resolved through symlinks (umu's <c>pfx</c> → the prefix folder), so paths of one install always compare equal.
    /// </summary>
    public static WineProcess? Inspect(int pid)
    {
        var start = StartTime((uint)pid);
        if (start is null) return null;
        lock (Gate)
            if (Cache.TryGetValue(pid, out var hit) && hit.Start == start) return hit.Process;

        var process = WineProcess.Parse(pid, ReadText($"/proc/{pid}/cmdline"), ReadText($"/proc/{pid}/environ"), ReadLink($"/proc/{pid}/cwd"), ReadLink($"/proc/{pid}/exe"));
        if (process is { Prefix.Length: > 0 } && RealPath(process.Prefix) is var real && real != process.Prefix)
        {
            var exe = process.ExePath.StartsWith(process.Prefix + "/", StringComparison.Ordinal) ? real + process.ExePath[process.Prefix.Length..] : process.ExePath;
            process = process with { Prefix = real, ExePath = exe };
        }
        lock (Gate) Cache[pid] = (start, process);
        return process;
    }

    /// <summary>Every process of this user that /proc shows (Linux programs and Wine programs).</summary>
    public static IEnumerable<WineProcess> All()
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories("/proc").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var dir in dirs)
            if (int.TryParse(Path.GetFileName(dir), out var pid) && Inspect(pid) is { } p) yield return p;
    }

    /// <summary>Running WoW game clients, oldest first.</summary>
    public static List<WineProcess> WowClients() =>
        All().Where(p => p.IsWow).OrderBy(p => StartTime((uint)p.Pid) ?? DateTime.MaxValue).ToList();

    /// <summary>A folder with its symlinks resolved (the path itself if it is not a link or does not exist).</summary>
    internal static string RealPath(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is null) return folder.TrimEnd('/');
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName.TrimEnd('/') ?? folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return folder;
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
